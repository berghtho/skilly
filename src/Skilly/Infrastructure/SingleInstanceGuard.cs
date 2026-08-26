using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Skilly.Infrastructure;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _focusEvent;

    public bool IsPrimary { get; }

    public EventWaitHandle FocusEvent => _focusEvent;

    public SingleInstanceGuard()
    {
        var token = UserToken();
        _mutex = new Mutex(initiallyOwned: true, name: $"Global\\Skilly.{token}.single-instance", out var createdNew);
        IsPrimary = createdNew;
        _focusEvent = new EventWaitHandle(initialState: false, mode: EventResetMode.AutoReset, name: $"Local\\Skilly.{token}.focus");
    }

    public static bool TrySignalFocus(TimeSpan? timeout = null)
    {
        var token = UserToken();
        var name = $"Local\\Skilly.{token}.focus";
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        do
        {
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                using (handle)
                {
                    handle.Set();
                }

                return true;
            }

            Thread.Sleep(200);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }

    private static string UserToken()
    {
        var raw = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes)[..16];
    }

    public void Dispose()
    {
        if (IsPrimary)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _focusEvent.Dispose();
    }
}

using System.Windows;
using System.Windows.Controls;

namespace Skilly;

public sealed class SkillListContainerStyleSelector : StyleSelector
{
    public Style? SkillRowStyle { get; set; }

    public Style? LibraryGroupRowStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
        => item is ViewModels.LibraryGroupRow ? LibraryGroupRowStyle : SkillRowStyle;
}

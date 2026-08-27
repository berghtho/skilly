# Payload identity is a reimplemented git tree hash

To detect Locally Modified installations and to verify Adoption, Skilly needs a content identity that can be compared against what a Source Provider serves without downloading the payload again. `Skills/GitTreeHasher.cs` reimplements git's SHA-1 blob/tree object hashing (mode `100644`/`40000`, ordinal entry ordering) so a local folder's hash is directly comparable to the tree SHA GitHub reports for the same folder. A private hash format was rejected because it could only ever compare Skilly to itself; comparing to the source is the whole point.

## Consequences

- SHA-1 is used deliberately for git compatibility, not security; it identifies content, it does not authenticate it.
- `MatchesFolder` retries with CRLF→LF normalization because git checkouts on Windows may rewrite line endings; equal-after-normalization counts as unmodified.
- Hashing refuses reparse points (see ADR-0001).

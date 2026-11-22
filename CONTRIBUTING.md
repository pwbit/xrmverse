# Contributing to xrmverse

Thanks for your interest in xrmverse. This page explains the license of contributions and the basic rules for a pull
request. How to build and test is in [Development](docs/development.md).

## License of contributions

xrmverse is licensed under the [Apache License, Version 2.0](LICENSE). Under section 5 of that license, any
contribution you intentionally submit is licensed under the same terms, without additional terms or conditions.

## Developer Certificate of Origin (DCO)

Every commit must be signed off, certifying the [Developer Certificate of Origin 1.1](https://developercertificate.org/):
that you wrote the change, or otherwise have the right to submit it under the project's license. Add the sign-off with
`git commit -s`, which appends a line like:

```
Signed-off-by: Your Name <you@example.com>
```

Use your real name and an address you can be reached at. We do not ask you to sign a Contributor License Agreement.

## Rules for a pull request

- No code, text, images or binaries copied from Microsoft products or documentation: reimplement behavior, never
  expression.
- New or upgraded dependencies must use a license that the license check accepts (`eng/licenses.sh`, run by CI), and the same pull request
  updates `THIRD_PARTY_NOTICES.md` (`eng/licenses.sh notices --out THIRD_PARTY_NOTICES.md`).
- Never commit Microsoft SDK assemblies (`.dll`, `.nupkg`) or real personal data.
- Code, comments, commits and documentation are in English.
- A pull request that adds, changes, fixes or removes a known issue, a limitation or a difference from Dataverse updates
  [Known issues and limitations](docs/known-issues.md) in the same pull request.
- Report security vulnerabilities privately, as described in [SECURITY.md](SECURITY.md), not in a public issue.

Do not use the xrmverse names or logos for a modified version: see [TRADEMARKS.md](TRADEMARKS.md).

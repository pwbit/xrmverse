# Community launch drafts

[← xrmverse documentation](../README.md#documentation)

Proposed copy for the Product Hunt launch on December 2, 2026. These are drafts for review, not posted announcements.
Check the actual launch status and replace the Product Hunt link with the verified public launch URL before posting.
Do not link the launch editor. Recheck downloads and compatibility against the version being demonstrated.

## Product Hunt maker comment

Hi everyone — we're building xrmverse, an open-source server for running Dataverse-compatible applications on infrastructure you control.

The goal is to reuse familiar APIs, .NET plug-ins and solution components while keeping control of data and operations. xrmverse also includes a CLI and an MCP server for agents working under the user's permissions.

Compatibility has limits: this is not every Power Platform service on a local server. Microsoft first-party Dynamics 365 solutions are not supported, and each application needs validation against the documented compatibility scope. The source tree can also be ahead of the current binary release.

Start here: https://github.com/pwbit/xrmverse
Public binary downloads: https://github.com/pwbit/xrmverse/releases/tag/v0.1.11
Compatibility: https://xrmverse.com/docs/dataverse-comparison.html
Known limits: https://xrmverse.com/docs/known-issues.html

We'd like to hear which application or integration you would evaluate first, and where the setup or compatibility documentation needs more detail. If you try it, please share your platform, version and a reproducible example with synthetic data through GitHub Issues.

## Developer community post

We're preparing xrmverse for its December 2 Product Hunt launch: an Apache 2.0 server for running Dataverse-compatible applications on your own infrastructure.

If you work with Dataverse APIs, .NET plug-ins or solutions and need a self-hosted option, we'd welcome feedback on the local Quick Start and the compatibility guide. There is no xrmverse per-user or per-capacity license fee; infrastructure costs and third-party terms still apply.

Repository and Quick Start: https://github.com/pwbit/xrmverse
Compatibility and limits: https://xrmverse.com/docs/dataverse-comparison.html

What would you need to verify before evaluating a self-hosted server for one of your applications?

## Suggested demo recording (60–90 seconds)

This is a recording plan; no hosted demo or video has been published by this change.

1. Show the installed CLI version and `/healthz`, then open `/manage` with an already configured local environment.
2. Show tables and an imported solution you own, using synthetic data. Never show signing keys, tokens or customer information.
3. Open the solution's model-driven app at `/apps` and show one supported view and form.
4. End on the compatibility guide and Quick Start. If demonstrating migration, record it separately against a licensed source environment and show verification, not only import completion.

Record the version used. Do not present features in the current source tree as available in v0.1.11 without checking them.

## Feedback and contribution paths

- Reproducible bugs and feature requests: [GitHub Issues](https://github.com/pwbit/xrmverse/issues).
- Code and documentation contributions: [Contributing](../CONTRIBUTING.md), including the DCO sign-off.
- Security reports: follow the private reporting channel in [SECURITY.md](../SECURITY.md).
- Candidate first contributions: clarify a failing setup step, improve an example for a supported client, or provide a small synthetic compatibility reproduction. Maintainers should validate scope before labelling work as a good first issue.

Submit to relevant community channels only after checking their self-promotion rules and tailoring the example to the audience. Ask for technical feedback rather than votes. No posts, issues, release publication or community settings changes are performed by these drafts.

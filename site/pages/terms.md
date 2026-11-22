# xrmverse — Terms of Use

## 1. Parties, scope and acceptance

These terms are between Power Builders Italia S.r.l., Via della Cipriana 29, 00046 Grottaferrata (RM), Italy, VAT IT17064131000 ("Power Builders", "we") and the organisation that registers on xrmverse.com or asks us for support ("you").

They cover the services we provide around xrmverse: the website xrmverse.com, the registration at xrmverse.com/get-started/, the downloads we host (release files, container image, Azure community gallery image) and support. You accept them by ticking the acceptance box on the registration form. If you accept on behalf of an organisation, you confirm you can bind it. The services are for businesses only, not for consumers.

**Software** means the xrmd command-line tool, the xrmd container image, the Azure virtual machine image and their source code, published by Power Builders. **Third-party components** means the software listed in section 3.

## 2. Software licence

The Software is licensed under the [Apache License, Version 2.0](https://www.apache.org/licenses/LICENSE-2.0). Your rights to use, copy, modify and distribute the Software, including in production, come from that licence, not from these terms, and these terms do not restrict them. Each copy of the Software includes the `LICENSE` and `NOTICE` files.

The Apache License does not grant rights to the names "xrmverse" and "xrmd" or to the xrmverse logos, which are trademarks of Power Builders. You may use them to describe the origin of the Software truthfully; do not use them as the name of a modified version, a product or a hosted service without our written permission. Do not remove copyright, trademark or non-affiliation notices.

## 3. Third-party components

The Software does not include the database server or the Microsoft SDK. When you run `xrmd server init`, you download them yourself and accept their licence terms directly with their owners. Power Builders is not a party to those terms. Other third-party software included in the Software is listed, with its licence, in `THIRD_PARTY_NOTICES.md`.

| Component | Licensor | When you accept it |
| --- | --- | --- |
| Power Apps / Dynamics 365 SDK (NuGet) | Microsoft | `--accept-microsoft-license` at init |
| SQL Server container image | Microsoft | `--accept-sql-server-eula` at init |
| Ubuntu 24.04 LTS | Canonical and upstream projects | included in the Azure image, open-source licences (on your own servers, you install it yourself) |
| Docker Engine | Docker, Inc. and upstream projects | included in the Azure image, open-source licences (on your own servers, you install it yourself) |

xrmverse is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft. Microsoft, Dataverse, Dynamics 365 and Power Apps are trademarks of the Microsoft group of companies.

## 4. Preview, warranty, support and liability

- **Preview**: the Software is a pre-release. Features may change or be removed, and we may stop publishing preview releases.
- **No warranty**: the Software is provided "as is", as stated in sections 7 and 8 of the Apache License. The services are also provided "as is", without warranties of any kind, to the extent permitted by law. You are responsible for backups and for securing your machines.
- **Support**: best effort, by e-mail to support@powerbuilders.it, with no response time commitment.
- **Liability**: for the services, our total liability is limited to EUR 100. We are not liable for indirect damages, lost data or lost profits. These limits do not apply to wilful misconduct or gross negligence (art. 1229 Italian Civil Code).
- **Infrastructure costs**: the Software is free; you pay for the hardware and for any cloud infrastructure you use, such as Azure.

## 5. Data, feedback, term and law

- **Data**: your data stays on your machines. How we handle personal data is described at [xrmverse.com/privacy](https://xrmverse.com/privacy/).
- **Feedback**: if you send us suggestions, we may use them freely without obligation to you. Code you contribute to the project is licensed under the Apache License, as described in `CONTRIBUTING.md`.
- **Term**: these terms last while you use the services. We may stop a service with 30 days notice published on xrmverse.com. Ending these terms does not end your rights under the Apache License.
- **Changes**: we may update these terms; the new version applies from its publication date.
- **Governing law and courts**: Italian law; exclusive jurisdiction of the Court of Velletri (Tribunale di Velletri), Italy.
- **Contact**: legal@powerbuilders.it.

Effective date: 17 November 2026.

# xrmverse server — Privacy Policy

## Summary

- xrmverse server runs on **your** machines: a virtual machine in your Azure subscription or your own servers, physical or virtual. We do not see your data.
- The software sends **no telemetry** to us.
- We receive the details you enter in the registration form on xrmverse.com (name, work e-mail, company, country) and what you send us in support requests. We do not otherwise learn who deploys or downloads xrmverse.
- We do not sell personal data.

## 1. Who we are

Power Builders Italia S.r.l., Via della Cipriana 29, 00046 Grottaferrata (RM), Italy, VAT IT17064131000 ("Power Builders", "we") publishes xrmverse server. For the personal data described in section 3 we are the data controller.

Privacy contact: privacy@powerbuilders.it.

Data Protection Officer: dpo@powerbuilders.it.

## 2. Scope

This policy covers how we distribute xrmverse server and the software itself:

- the installation on your own servers, physical or virtual, with the xrmd command-line tool;
- the **Deploy to Azure** template (`https://xrmverse.com/deploy/azure/azuredeploy.json`) and the virtual machine image in our public Azure Compute Gallery;
- the xrmd command-line tool and release files, published on GitHub;
- the xrmd container image, published on the GitHub Container Registry (`ghcr.io`);
- this website.

The software runs entirely on the customer's own machines, in the customer's Azure subscription or data centre. Power Builders does not host it, has no access to those machines and receives none of the data stored in them.

Microsoft processes data about your Azure deployment under its own privacy statement: [Microsoft Privacy Statement](https://privacy.microsoft.com/privacystatement). GitHub processes data about downloads from GitHub and `ghcr.io` under the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

## 3. Data we receive as publisher

We receive personal data only through the registration form on xrmverse.com and through support requests you send us. Deploying the template, using the gallery image or downloading the software does not send us your name, e-mail, subscription ID or IP address. Azure and GitHub may show us only aggregate numbers, such as download counts, that do not identify you.

| Data | Source | Purpose | Legal basis |
| --- | --- | --- | --- |
| First and last name, work e-mail, company, country; role and intended use if you give them; date and time you accepted the Terms of Use and the versions of it and of this policy you were shown | Registration form on xrmverse.com (`/get-started/`) | Enter into the Preview Evaluation Agreement and keep proof of its acceptance; know who evaluates xrmverse and contact you about the preview | Contract (art. 6(1)(b) GDPR) and legitimate interest (art. 6(1)(f)) |
| Name, e-mail and content of your message | Support requests | Provide support | Contract or pre-contractual steps (art. 6(1)(b)) |

We do not sell personal data and do not use it for profiling or automated decisions.

## 4. Data processed by the software on your machines

You are the controller of all data you store in or process with xrmd. It stays on your machines and their disks, in your Azure subscription or your data centre; we never receive it.

The software sends no telemetry to Power Builders. Its traces and metrics stay on the machine: a local `/metrics` endpoint, and an OTLP export only to an endpoint you configure yourself.

The software connects to third parties only when you run the commands that need them:

| Connection | When | Recipient |
| --- | --- | --- |
| GitHub (`ghcr.io` and release files) | install and `xrmd server upgrade` | GitHub |
| NuGet.org (SDK download) | `xrmd server init` / `xrmd sdk install` | Microsoft |
| Container registry (database server image) | first `xrmd server up` | Microsoft Container Registry |
| Certificate authority | only with `--tls acme` | the ACME provider you choose |
| Microsoft Entra ID sign-in | only with `--auth entra` | Microsoft |
| Ubuntu security updates | operating system updates | Canonical |

These providers process the request data (such as the IP address of your machine) under their own privacy policies.

## 5. Our website

The xrmverse website and documentation are static pages hosted on GitHub Pages. We set no cookies and use no analytics or tracking. The registration form uses no cookies either; the service that receives it uses your IP address only in memory, for a few minutes, to limit repeated submissions, and does not store it. GitHub, as hosting provider, logs the IP address of visitors for security under the [GitHub Privacy Statement](https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement).

## 6. Retention, transfers, security and your rights

- **Retention**: registration data is kept for 24 months after our last contact with you; support requests are kept for 12 months after the request is closed.
- **Processors**: the registration form is received by a service we run on Microsoft Azure and stored in Azure Table Storage in the West Europe region (European Union); Microsoft acts as our processor.
- **Transfers**: we store data in the European Union. Microsoft and GitHub may process deployment and download data outside the EU under their own safeguards (standard contractual clauses / EU-US Data Privacy Framework).
- **Security**: access to registration data and support mailboxes is limited to Power Builders staff who need it, with multi-factor authentication. No transmission or storage is completely secure; you are responsible for securing your own machines.
- **Your rights**: you can ask for access, rectification, erasure, restriction, portability and object to processing (art. 15–21 GDPR) by writing to privacy@powerbuilders.it. You can also lodge a complaint with the Garante per la protezione dei dati personali ([garanteprivacy.it](https://www.garanteprivacy.it)).
- **Data breach**: if a breach affects personal data we hold, we notify the supervisory authority within 72 hours and the people affected when the risk to them is high (art. 33–34 GDPR).
- **Children**: xrmverse server is for businesses and is not directed at people under 18. We delete any data of minors we discover.
- **Changes**: we publish any change to this policy at this URL, with a new effective date.

Effective date: 16 November 2026.

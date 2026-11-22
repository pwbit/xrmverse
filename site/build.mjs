// Static generator for the xrmverse website (GitHub Pages).
//
// Input:  site/site.config.json, site/assets/*, deploy/azure/azuredeploy.json, README.md (the "Documentation" index) and docs/*.md.
// Output: _site/ — index.html (home), docs/index.html (documentation index) and docs/<page>.html per docs page.
//
// Internal material is never published and never linked: everything under docs/ that is not a docs page or an image
// (internal folders), the docs pages listed in `unpublished` (site.config.json), the Markdown between the lines
// "<!-- private -->" and "<!-- /private -->", and, while `repositoryPublic` is false, every other repository file. A link
// to any of them is rendered as its plain text, and the build fails if a generated page still links to one. All site
// links are relative, so the site works under any base path (https://<org>.github.io/<repo>/ or a custom domain).
//
// Search engines: every published page carries a canonical URL and Open Graph / Twitter card metadata built from
// `siteUrl`, and the build writes sitemap.xml (every published page, nothing internal) and robots.txt (pointing to it).
// Internal pages are never generated, so they cannot be crawled; the 404 page is marked noindex.

import { readFile, writeFile, mkdir, readdir, cp, rm } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Marked } from 'marked';

const siteDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(siteDir, '..');
const docsDir = path.join(repoRoot, 'docs');
const outDir = path.resolve(process.argv[2] ?? path.join(repoRoot, '_site'));

/** Markdown without its private blocks (from a line "<!-- private -->" to a line "<!-- /private -->"). */
function withoutPrivate(markdown) {
  return markdown.replace(/^<!-- private -->\r?\n[\s\S]*?^<!-- \/private -->\r?\n?/gm, '');
}

const config = JSON.parse(await readFile(path.join(siteDir, 'site.config.json'), 'utf8'));
// Mermaid is self-hosted (copied from the npm package at build time): the site makes no third-party requests.
const MERMAID_SOURCE = path.join(siteDir, 'node_modules', 'mermaid', 'dist', 'mermaid.min.js');
const unpublished = new Set(config.unpublished ?? []);
/** rewriteHref's answer for a link to internal material: the link is rendered as its text only. */
const INTERNAL = Symbol('internal');
/** The public address of the site (no trailing slash), used for canonical URLs, Open Graph, sitemap.xml and robots.txt. */
const siteUrl = String(config.siteUrl ?? '').replace(/\/+$/, '');
if (!/^https:\/\/[^/]+$/.test(siteUrl)) throw new Error('site.config.json: siteUrl must be an https origin, e.g. https://xrmverse.com');
/** Site paths of the generated pages that go into sitemap.xml, in build order. */
const sitemapPaths = [];
/** The absolute URL of a site path ('' = home, 'docs/' = docs index, 'docs/install.html', 'privacy/'). */
const absoluteUrl = (sitePath) => `${siteUrl}/${sitePath}`;

const TRADEMARK_NOTICE =
  'xrmverse is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft. ' +
  'Microsoft, Dataverse, Dynamics 365 and Power Apps are trademarks of the Microsoft group of companies.';

// ---------- helpers ----------

const escapeHtml = (s) =>
  String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

const stripTags = (html) =>
  html
    .replace(/<[^>]*>/g, '')
    .replace(/&quot;/g, '"')
    .replace(/&#39;/g, "'")
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&amp;/g, '&');

/** GitHub-style heading anchor, so links written for github.com keep working on the site. */
function slugify(text) {
  return text
    .toLowerCase()
    .trim()
    .replace(/[^\p{L}\p{N}\s_-]/gu, '')
    .replace(/\s/g, '-');
}

/** A repository file on GitHub, or INTERNAL while the repository is private (the link would 404 for visitors). */
function blobUrl(repoPath, isDir) {
  if (!config.repositoryPublic) return INTERNAL;
  const kind = isDir ? 'tree' : 'blob';
  return `${config.repositoryUrl}/${kind}/${config.defaultBranch}/${repoPath.replace(/^\/+/, '')}`;
}

/** The published pages: every docs/*.md file (not subfolders) that is not listed in `unpublished`. */
async function listDocPages() {
  const entries = await readdir(docsDir, { withFileTypes: true });
  return entries
    .filter((e) => e.isFile() && e.name.endsWith('.md') && !unpublished.has(e.name.replace(/\.md$/, '')))
    .map((e) => e.name.replace(/\.md$/, ''))
    .sort();
}

/**
 * Rewrites a link found in a markdown file at `fromRepoPath` (relative to the repository root).
 * - docs/<page>.md          -> <page>.html (relative to the site's docs/ folder)
 * - docs/images/...         -> images/... (copied to the site)
 * - other paths under docs/ (internal folders), unpublished docs pages -> INTERNAL (rendered as plain text)
 * - anything else in the repo (README, src, ...) -> the file on GitHub, or INTERNAL while the repository is private
 * - external URLs and in-page anchors are left as they are.
 * `siteDocsPrefix` is how the page reaches the site's docs/ folder ('' from docs pages, 'docs/' from the home page).
 */
function rewriteHref(href, fromRepoPath, published, siteDocsPrefix = '') {
  // Absolute links into the private repository (issues, pull requests, files) would 404 for visitors.
  if (!config.repositoryPublic && href && isRepositoryUrl(href)) return INTERNAL;
  if (!href || /^[a-z][a-z0-9+.-]*:/i.test(href) || href.startsWith('#') || href.startsWith('//')) return href;
  const [target, hash] = splitHash(href);
  if (!target) return href;
  const resolved = path.posix.normalize(path.posix.join(path.posix.dirname(fromRepoPath), decodeURI(target)));
  const anchor = hash ? `#${hash}` : '';
  const pageMatch = /^docs\/([^/]+)\.md$/.exec(resolved);
  if (pageMatch && published.has(pageMatch[1])) return `${siteDocsPrefix}${pageMatch[1]}.html${anchor}`;
  if (pageMatch) return INTERNAL;
  if (resolved === 'docs' || resolved === 'docs/') return `${siteDocsPrefix}index.html${anchor}`;
  // The README's documentation index is the site's docs index page.
  if (resolved === 'README.md' && (!hash || hash === 'documentation')) return `${siteDocsPrefix}index.html`;
  if (resolved.startsWith('docs/images/')) return `${siteDocsPrefix}${resolved.slice('docs/'.length)}${anchor}`;
  if (resolved.startsWith('docs/')) return INTERNAL;
  const isDir = target.endsWith('/') || !path.posix.extname(resolved);
  const url = blobUrl(resolved, isDir);
  return url === INTERNAL ? INTERNAL : url + anchor;
}

function isRepositoryUrl(url) {
  const repo = config.repositoryUrl.toLowerCase().replace(/\/+$/, '');
  const u = url.toLowerCase();
  return u === repo || u.startsWith(`${repo}/`) || u.startsWith(`${repo}#`) || u.startsWith(`${repo}?`);
}

function splitHash(href) {
  const i = href.indexOf('#');
  return i < 0 ? [href, ''] : [href.slice(0, i), href.slice(i + 1)];
}

/** A Marked instance for one page: rewrites links, gives headings GitHub ids, collects the h2 outline. */
function createRenderer(fromRepoPath, published) {
  const state = { headings: [], hasMermaid: false, title: null, slugs: new Map(), images: new Set() };
  const marked = new Marked({ gfm: true });
  marked.use({
    walkTokens(token) {
      if (token.type === 'link' || token.type === 'image') token.href = rewriteHref(token.href, fromRepoPath, published);
      if (typeof token.href === 'string' && token.href.startsWith('images/')) state.images.add(splitHash(token.href)[0]);
    },
    renderer: {
      link({ href, tokens }) {
        // A link to internal material keeps its text and loses the link.
        return href === INTERNAL ? this.parser.parseInline(tokens) : false;
      },
      image({ href, text }) {
        return href === INTERNAL ? escapeHtml(text) : false;
      },
      heading({ tokens, depth }) {
        const inner = this.parser.parseInline(tokens);
        const text = stripTags(inner);
        let slug = slugify(text);
        const seen = state.slugs.get(slug) ?? 0;
        state.slugs.set(slug, seen + 1);
        if (seen) slug = `${slug}-${seen}`;
        if (depth === 1 && !state.title) state.title = text;
        if (depth === 2) state.headings.push({ slug, text });
        const link = depth > 1 ? `<a class="anchor" href="#${slug}" aria-hidden="true" tabindex="-1">#</a>` : '';
        return `<h${depth} id="${escapeHtml(slug)}">${inner}${link}</h${depth}>\n`;
      },
      code({ text, lang }) {
        if ((lang ?? '').trim() === 'mermaid') {
          state.hasMermaid = true;
          return `<pre class="mermaid">${escapeHtml(text)}</pre>\n`;
        }
        return false;
      },
    },
  });
  // Wrap tables so wide ones scroll horizontally instead of widening the page.
  marked.use({
    hooks: {
      postprocess(html) {
        return html.replace(/<table>/g, '<div class="table-wrap"><table>').replace(/<\/table>/g, '</table></div>');
      },
    },
  });
  return { marked, state };
}

// ---------- the documentation index, read from README.md "## Documentation" ----------

/**
 * Groups and order of the docs pages, from the README's Documentation section: each **bold** line starts a group, each
 * table row "| [Title](docs/page.md) | What it covers |" is a page. Pages not listed there go into a "More" group.
 */
function readDocIndex(readme, published) {
  const start = readme.indexOf('\n## Documentation');
  const end = start < 0 ? -1 : readme.indexOf('\n## ', start + 5);
  const section = start < 0 ? '' : readme.slice(start, end < 0 ? undefined : end);
  const groups = [];
  let current = null;
  const listed = new Set();
  for (const line of section.split('\n')) {
    const group = /^\*\*(.+)\*\*\s*$/.exec(line.trim());
    if (group) {
      current = { name: group[1], pages: [] };
      groups.push(current);
      continue;
    }
    const row = /^\|\s*\[([^\]]+)\]\(docs\/([^/)#]+)\.md\)\s*\|\s*(.*?)\s*\|\s*$/.exec(line.trim());
    if (row && current && published.has(row[2]) && !listed.has(row[2])) {
      current.pages.push({ name: row[2], title: row[1], summary: row[3] });
      listed.add(row[2]);
    }
  }
  const rest = [...published].filter((p) => !listed.has(p));
  if (rest.length) groups.push({ name: 'More', pages: rest.map((name) => ({ name, title: null, summary: '' })) });
  return groups.filter((g) => g.pages.length);
}

// ---------- page layout ----------

/**
 * The <head> of a page. `sitePath` is the page's path on the site (see absoluteUrl): it gives the canonical URL and
 * Open Graph URL, and puts the page in sitemap.xml. `noindex` pages (the 404 page) get no canonical URL and stay out
 * of the sitemap.
 */
function head({ title, description, root, sitePath = null, noindex = false, type = 'website' }) {
  if (sitePath !== null && !noindex && !sitemapPaths.includes(sitePath)) sitemapPaths.push(sitePath);
  const url = sitePath !== null && !noindex ? absoluteUrl(sitePath) : null;
  const image = absoluteUrl('assets/og-image.png');
  const seo = noindex
    ? '<meta name="robots" content="noindex">'
    : `${url ? `<link rel="canonical" href="${escapeHtml(url)}">\n` : ''}<meta property="og:site_name" content="${escapeHtml(config.siteTitle)}">
<meta property="og:type" content="${type}">
<meta property="og:title" content="${escapeHtml(title)}">
<meta property="og:description" content="${escapeHtml(description)}">
${url ? `<meta property="og:url" content="${escapeHtml(url)}">\n` : ''}<meta property="og:image" content="${escapeHtml(image)}">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="xrmverse logo">
<meta name="twitter:card" content="summary_large_image">`;
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(title)}</title>
<meta name="description" content="${escapeHtml(description)}">
${seo}
<meta name="color-scheme" content="light dark">
<link rel="icon" type="image/png" sizes="32x32" href="${root}assets/favicon-32.png">
<link rel="apple-touch-icon" href="${root}assets/apple-touch-icon.png">
<link rel="stylesheet" href="${root}assets/style.css">
</head>`;
}

/** sitemap.xml: every published page (internal pages are never generated, the 404 page is left out). */
function sitemapXml() {
  const urls = [...sitemapPaths].sort().map((p) => `  <url><loc>${escapeHtml(absoluteUrl(p))}</loc></url>`).join('\n');
  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>
`;
}

function robotsTxt() {
  return `User-agent: *
Allow: /

Sitemap: ${absoluteUrl('sitemap.xml')}
`;
}

function topNav({ root, active }) {
  return `<header class="topnav">
  <a class="brand" href="${root}index.html" aria-label="xrmverse home">
    <picture>
      <source srcset="${root}assets/logo-dark.png" media="(prefers-color-scheme: dark)">
      <img src="${root}assets/logo-light.png" alt="xrmverse" width="150" height="38">
    </picture>
  </a>
  <nav aria-label="Main">
    <a href="${root}docs/index.html"${active === 'docs' ? ' aria-current="page"' : ''}>Docs</a>
  </nav>
</header>`;
}

/** Standalone pages linked from the footer, in this order, when site/pages/<name>.md exists. */
const FOOTER_PAGES = [
  ['privacy', 'Privacy'],
  ['terms', 'Terms'],
];
let footerPages = [];

function footer(root = '') {
  const links = footerPages.length
    ? `<p class="footer-links">${footerPages.map(([name, label]) => `<a href="${root}${name}/index.html">${label}</a>`).join(' · ')}</p>`
    : '';
  return `<footer class="footer">${links}<p>${escapeHtml(TRADEMARK_NOTICE)}</p></footer>`;
}

function primaryButton() {
  if (config.appSourceUrl) {
    return `<a class="button" href="${escapeHtml(config.appSourceUrl)}" rel="noopener">Get it on Microsoft AppSource</a>`;
  }
  // No AppSource offer yet (parked). With the registration service configured, the call to action is the registration
  // page; otherwise the "Deploy to Azure" docs page, when it is published.
  if (config.registrationEndpoint) {
    return `<a class="button" href="get-started/index.html">Get xrmverse</a>`;
  }
  // A text button, not Microsoft's "Deploy to Azure" badge image (legal guardrails §7).
  // Next to it, "Deploy on your metal" opens the install guide for your own physical or virtual servers.
  if (!unpublished.has('deploy-to-azure')) {
    const metal = unpublished.has('install') ? '' : `\n    <a class="button" href="docs/install.html">Deploy on your metal</a>`;
    return `<a class="button" href="docs/deploy-to-azure.html">Deploy to Azure</a>${metal}`;
  }
  return `<a class="button is-pending" href="#" aria-disabled="true" data-placeholder="appsource">Coming soon on Microsoft AppSource</a>`;
}

const AZURE_PORTAL_DEPLOY_URL =
  'https://portal.azure.com/#create/Microsoft.Template/uri/' + encodeURIComponent('https://xrmverse.com/deploy/azure/azuredeploy.json');
const RELEASES_URL = 'https://github.com/pwbit/xrmverse-releases/releases';

/** The "Effective date: <date>." line of a standalone page (site/pages/<name>.md): the version the visitor accepts. */
async function effectiveDate(name) {
  const markdown = await readFile(path.join(siteDir, 'pages', `${name}.md`), 'utf8');
  const match = /^Effective date: (.+?)\.?\s*$/m.exec(markdown);
  if (!match) throw new Error(`site/pages/${name}.md has no "Effective date:" line`);
  return match[1];
}

/**
 * The registration page: the form posts to the registration service, which creates a
 * lead; the links are shown after a successful submit. Built only when `registrationEndpoint` is set.
 */
async function getStartedPage() {
  const privacyVersion = await effectiveDate('privacy');
  const termsVersion = await effectiveDate('terms');
  const field = (name, label, { type = 'text', required = true, autocomplete = '', max = 100 } = {}) =>
    `<label class="field"><span>${label}${required ? '' : ' <em>(optional)</em>'}</span>` +
    `<input name="${name}" type="${type}" maxlength="${max}"${required ? ' required' : ''}${autocomplete ? ` autocomplete="${autocomplete}"` : ''}></label>`;
  return `${head({ title: 'Get xrmverse — xrmverse', description: config.description, root: '../', sitePath: 'get-started/' })}
<body class="page">
${topNav({ root: '../', active: 'get-started' })}
<main class="page-content"><article class="markdown get-started">
<h1>Get xrmverse</h1>
<p class="lead">Tell us who you are, accept the terms of use and you get the links to deploy xrmverse server on Azure or install it on your own servers.</p>
<form id="registration" class="registration" data-endpoint="${escapeHtml(config.registrationEndpoint)}"
      data-privacy-version="${escapeHtml(privacyVersion)}" data-terms-version="${escapeHtml(termsVersion)}" novalidate>
  <div class="field-row">
    ${field('firstName', 'First name', { autocomplete: 'given-name', max: 50 })}
    ${field('lastName', 'Last name', { autocomplete: 'family-name', max: 50 })}
  </div>
  ${field('email', 'Work e-mail', { type: 'email', autocomplete: 'email' })}
  <div class="field-row">
    ${field('company', 'Company', { autocomplete: 'organization' })}
    ${field('country', 'Country', { autocomplete: 'country-name', max: 80 })}
  </div>
  ${field('jobTitle', 'Role', { required: false, autocomplete: 'organization-title' })}
  <label class="field"><span>How will you use xrmverse? <em>(optional)</em></span><textarea name="usage" rows="3" maxlength="1000"></textarea></label>
  <label class="field honeypot" aria-hidden="true"><span>Website</span><input name="website" type="text" tabindex="-1" autocomplete="off"></label>
  <label class="check"><input name="acceptPrivacy" type="checkbox" required> <span>I have read the <a href="../privacy/index.html" target="_blank" rel="noopener">Privacy Policy</a> (effective ${escapeHtml(privacyVersion)}).</span></label>
  <label class="check"><input name="acceptTerms" type="checkbox" required> <span>I accept the <a href="../terms/index.html" target="_blank" rel="noopener">Terms of Use</a> (effective ${escapeHtml(termsVersion)}) on behalf of my organisation. The software itself is licensed under the Apache License 2.0.</span></label>
  <p class="form-error" role="alert" hidden></p>
  <div class="actions"><button class="button" type="submit">Get the links</button></div>
</form>
<section id="links" class="links" hidden>
<h2>Thank you. Here are your links</h2>
<ul class="card-list">
<li><a href="${AZURE_PORTAL_DEPLOY_URL}" rel="noopener"><strong>Deploy to Azure</strong><span>Opens the Azure portal with the xrmverse server template. Read the guide below first.</span></a></li>
<li><a href="../docs/deploy-to-azure.html"><strong>Deploy to Azure guide</strong><span>Prerequisites, parameters, first start and upgrades.</span></a></li>
<li><a href="../docs/install.html"><strong>Install on your own servers</strong><span>Physical or virtual machines with Docker: the xrmd command-line tool does the rest.</span></a></li>
<li><a href="${RELEASES_URL}" rel="noopener"><strong>Releases</strong><span>The xrmd command-line tool for Linux, macOS and Windows, with SHA-256 checksums.</span></a></li>
</ul>
</section>
</article></main>
${footer('../')}
<script src="../assets/register.js" defer></script>
</body>
</html>
`;
}

function homePage() {
  return `${head({ title: `${config.siteTitle} — ${config.tagline}`, description: config.description, root: '', sitePath: '' })}
<body class="home">
${topNav({ root: '', active: 'home' })}
<main class="hero">
  <picture>
    <source srcset="assets/symbol-dark.png" media="(prefers-color-scheme: dark)">
    <img class="hero-symbol" src="assets/symbol-light.png" alt="" width="160" height="160">
  </picture>
  <h1>${escapeHtml(config.tagline)}</h1>
  <p class="lead">${escapeHtml(config.description)}</p>
  <div class="actions">
    ${primaryButton()}
  </div>
  <p class="secondary"><a href="docs/index.html">Read the docs →</a></p>
</main>
${footer()}
</body>
</html>
`;
}

function sidebar(groups, activePage, titles) {
  const items = groups
    .map(
      (g) => `<div class="side-group">
  <p class="side-title">${escapeHtml(g.name)}</p>
  <ul>${g.pages
    .map((p) => {
      const current = p.name === activePage ? ' aria-current="page"' : '';
      return `<li><a href="${p.name}.html"${current}>${escapeHtml(p.title ?? titles.get(p.name) ?? p.name)}</a></li>`;
    })
    .join('')}</ul>
</div>`,
    )
    .join('\n');
  return `<details class="side-toggle"><summary>Documentation menu</summary></details>
<aside class="sidebar" aria-label="Documentation">
  <a class="side-home${activePage === null ? ' is-current' : ''}" href="index.html">Overview</a>
  ${items}
</aside>`;
}

function docsShell({ title, description, body, groups, activePage, titles, outline, hasMermaid, sourcePath, sitePath }) {
  const toc = outline.length
    ? `<nav class="toc" aria-label="On this page"><p class="side-title">On this page</p><ul>${outline
        .map((h) => `<li><a href="#${escapeHtml(h.slug)}">${escapeHtml(h.text)}</a></li>`)
        .join('')}</ul></nav>`
    : '';
  const mermaid = hasMermaid
    ? `<script src="../assets/vendor/mermaid.min.js"></script>
<script>
const dark = window.matchMedia('(prefers-color-scheme: dark)').matches;
mermaid.initialize({ startOnLoad: true, theme: dark ? 'dark' : 'neutral', securityLevel: 'strict' });
</script>`
    : '';
  const source = sourcePath && config.repositoryPublic
    ? `<p class="edit"><a href="${blobUrl(sourcePath)}" rel="noopener">View this page on GitHub</a></p>`
    : '';
  return `${head({ title, description, root: '../', sitePath, type: activePage ? 'article' : 'website' })}
<body class="docs">
${topNav({ root: '../', active: 'docs' })}
<div class="docs-layout">
${sidebar(groups, activePage, titles)}
<main class="content">
<article class="markdown">
${body}
</article>
${source}
</main>
${toc}
</div>
${footer('../')}
<script>
// Narrow windows: the "Documentation menu" toggle shows or hides the sidebar.
const t = document.querySelector('.side-toggle');
t?.addEventListener('toggle', () => document.body.classList.toggle('side-open', t.open));
</script>
${mermaid}
</body>
</html>
`;
}

// ---------- build ----------

async function build() {
  await rm(outDir, { recursive: true, force: true });
  await mkdir(path.join(outDir, 'docs'), { recursive: true });

  await cp(path.join(siteDir, 'assets'), path.join(outDir, 'assets'), { recursive: true });
  await writeFile(path.join(outDir, '.nojekyll'), '');
  await mkdir(path.join(outDir, 'assets', 'vendor'), { recursive: true });
  await cp(MERMAID_SOURCE, path.join(outDir, 'assets', 'vendor', 'mermaid.min.js'));

  // The "Deploy to Azure" ARM template, served next to its docs page and only once that page is published
  // (docs/deploy-to-azure.md). GitHub Pages sends Access-Control-Allow-Origin: *, which the Azure portal needs.
  if (!unpublished.has('deploy-to-azure')) {
    await mkdir(path.join(outDir, 'deploy', 'azure'), { recursive: true });
    await cp(path.join(repoRoot, 'deploy', 'azure', 'azuredeploy.json'), path.join(outDir, 'deploy', 'azure', 'azuredeploy.json'));
  }

  // Standalone pages (site/pages/<name>.md -> <name>/index.html), linked from the footer, not from the menu.
  const pagesDir = path.join(siteDir, 'pages');
  const standalone = existsSync(pagesDir)
    ? (await readdir(pagesDir)).filter((f) => f.endsWith('.md')).map((f) => f.replace(/\.md$/, ''))
    : [];
  footerPages = FOOTER_PAGES.filter(([name]) => standalone.includes(name));

  const pages = await listDocPages();
  const published = new Set(pages);
  const readme = withoutPrivate(await readFile(path.join(repoRoot, 'README.md'), 'utf8'));
  const groups = readDocIndex(readme, published);

  // First pass: render every page (titles are needed by the sidebar of every other page).
  const rendered = new Map();
  const titles = new Map();
  for (const name of pages) {
    const sourcePath = `docs/${name}.md`;
    const markdown = withoutPrivate(await readFile(path.join(repoRoot, sourcePath), 'utf8'));
    const { marked, state } = createRenderer(sourcePath, published);
    const body = marked.parse(markdown);
    titles.set(name, state.title ?? name);
    rendered.set(name, { body, state, sourcePath });
  }

  // Only the images that published pages show or link to (an image used only by internal pages stays internal).
  const images = new Set([...rendered.values()].flatMap(({ state }) => [...state.images]));
  for (const image of images) {
    const from = path.join(docsDir, image);
    if (!existsSync(from)) continue;
    await mkdir(path.dirname(path.join(outDir, 'docs', image)), { recursive: true });
    await cp(from, path.join(outDir, 'docs', image));
  }

  for (const [name, { body, state, sourcePath }] of rendered) {
    const entry = groups.flatMap((g) => g.pages).find((p) => p.name === name);
    const html = docsShell({
      title: `${titles.get(name)} — xrmverse docs`,
      description: entry?.summary ? stripTags(new Marked().parseInline(entry.summary)) : config.description,
      body,
      groups,
      activePage: name,
      titles,
      outline: state.headings,
      hasMermaid: state.hasMermaid,
      sourcePath,
      sitePath: `docs/${name}.html`,
    });
    await writeFile(path.join(outDir, 'docs', `${name}.html`), html);
  }

  // Documentation index: the README's groups with each page's summary.
  const inline = new Marked({ gfm: true });
  inline.use({
    walkTokens(token) {
      // The summaries' links are removed anyway (stripLinks), internal ones included.
      if (token.type === 'link') {
        const href = rewriteHref(token.href, 'README.md', published);
        token.href = href === INTERNAL ? '' : href;
      }
    },
  });
  const indexBody = `<h1 id="documentation">Documentation</h1>
<p class="lead">How to install, run and build on xrmverse. Each page describes the exact behaviour of xrmverse, including its known deviations from Dataverse.</p>
${groups
  .map(
    (g) => `<h2 id="${slugify(g.name)}">${escapeHtml(g.name)}</h2>
<ul class="card-list">${g.pages
      .map(
        (p) => `<li><a href="${p.name}.html"><strong>${escapeHtml(p.title ?? titles.get(p.name))}</strong>${
          p.summary ? `<span>${stripLinks(inline.parseInline(p.summary))}</span>` : ''
        }</a></li>`,
      )
      .join('')}</ul>`,
  )
  .join('\n')}`;
  await writeFile(
    path.join(outDir, 'docs', 'index.html'),
    docsShell({
      title: 'Documentation — xrmverse',
      description: config.description,
      body: indexBody,
      groups,
      activePage: null,
      titles,
      outline: groups.map((g) => ({ slug: slugify(g.name), text: g.name })),
      hasMermaid: false,
      sourcePath: null,
      sitePath: 'docs/',
    }),
  );

  for (const name of standalone) {
    const sourcePath = `site/pages/${name}.md`;
    const { marked, state } = createRenderer(sourcePath, published);
    const body = marked.parse(withoutPrivate(await readFile(path.join(repoRoot, sourcePath), 'utf8')));
    await mkdir(path.join(outDir, name), { recursive: true });
    await writeFile(
      path.join(outDir, name, 'index.html'),
      `${head({ title: `${state.title ?? name} — xrmverse`, description: config.description, root: '../', sitePath: `${name}/` })}
<body class="page">
${topNav({ root: '../', active: name })}
<main class="page-content"><article class="markdown">
${body}
</article></main>
${footer('../')}
</body>
</html>
`,
    );
  }

  if (config.registrationEndpoint) {
    await mkdir(path.join(outDir, 'get-started'), { recursive: true });
    await writeFile(path.join(outDir, 'get-started', 'index.html'), await getStartedPage());
  }

  await writeFile(path.join(outDir, 'index.html'), homePage());
  await writeFile(
    path.join(outDir, '404.html'),
    `${head({ title: 'Page not found — xrmverse', description: config.description, root: './', noindex: true })}
<body class="home">
${topNav({ root: './', active: 'home' })}
<main class="hero"><h1>Page not found</h1><p class="lead">The page you are looking for does not exist.</p>
<div class="actions"><a class="button" href="./index.html">Back to home</a></div></main>
${footer()}
</body></html>
`,
  );

  await writeFile(path.join(outDir, 'sitemap.xml'), sitemapXml());
  await writeFile(path.join(outDir, 'robots.txt'), robotsTxt());

  await assertNoInternalLinks();
  console.log(`Built ${pages.length} docs pages into ${path.relative(repoRoot, outDir) || outDir}`);
}

/**
 * Safety net: fails the build if a generated file links to internal material (an unpublished page, an internal docs folder or,
 * while the repository is private, the repository on GitHub) or if an unpublished page was generated anyway.
 */
async function assertNoInternalLinks() {
  const problems = [];
  for (const file of await readdir(outDir, { recursive: true })) {
    if (!file.endsWith('.html')) continue;
    const name = path.basename(file, '.html');
    if (path.dirname(file) === 'docs' && unpublished.has(name)) problems.push(`${file}: unpublished page was generated`);
    const html = await readFile(path.join(outDir, file), 'utf8');
    for (const [, url] of html.matchAll(/\b(?:href|src|srcset)="([^"]*)"/gi)) {
      const u = url.toLowerCase();
      const target = path.posix.basename(splitHash(u)[0], '.html');
      if (!/^[a-z][a-z0-9+.-]*:/.test(u) && /(^|\/)docs\/[^#]*\//.test(u) && !/(^|\/)docs\/images\//.test(u))
        problems.push(`${file}: links to an internal docs folder (${url})`);
      else if (!config.repositoryPublic && isRepositoryUrl(u))
        problems.push(`${file}: links to the private repository (${url})`);
      else if (!/^[a-z][a-z0-9+.-]*:/.test(u) && u.endsWith('.html') && unpublished.has(target))
        problems.push(`${file}: links to unpublished page ${target} (${url})`);
      else if (/\.md(#|$)/.test(u) && !/^[a-z][a-z0-9+.-]*:/.test(u)) problems.push(`${file}: unresolved markdown link (${url})`);
    }
  }
  for (const p of sitemapPaths) {
    const target = path.posix.basename(p, '.html');
    if (p.startsWith('docs/') && unpublished.has(target)) problems.push(`sitemap.xml: lists unpublished page ${target}`);
  }
  if (problems.length) {
    console.error(`Internal links found in the generated site:\n  ${problems.join('\n  ')}`);
    process.exit(1);
  }
}

/** The index cards are links themselves, so nested links in a summary become plain text. */
function stripLinks(html) {
  return html.replace(/<a\b[^>]*>/g, '').replace(/<\/a>/g, '');
}

await build();

import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it } from 'vitest';
import { setUiLanguage } from '../shell/i18n/t';
import { server } from '../test/server';
import { WebResourcePage } from './WebResourcePage';

afterEach(() => setUiLanguage(undefined));

describe('WebResourcePage', () => {
  it('webresource_page_renders_full_size_with_data', async () => {
    const asked: string[] = [];
    server.use(
      http.get('*/WebResources/op365_/pages/decision.html', ({ request }) => {
        asked.push(new URL(request.url).search);
        return new HttpResponse('<html></html>', { headers: { 'Content-Type': 'text/html' } });
      }),
    );
    render(<WebResourcePage name="op365_/pages/decision.html" data="action=x&id=1" />);
    const frame = (await screen.findByTitle('Web resource op365_/pages/decision.html')) as HTMLIFrameElement;
    expect(frame.getAttribute('src')).toBe('/WebResources/op365_/pages/decision.html?data=action%3Dx%26id%3D1');
    expect(frame.style.width).toBe('100%');
    expect(frame.style.height).toBe('100%');
    // Same origin: nothing sandboxed, so the page reaches parent.Xrm.
    expect(frame.hasAttribute('sandbox')).toBe(false);
    expect(asked).toEqual(['?data=action%3Dx%26id%3D1']);
  });

  it('unknown_webresource_shows_message', async () => {
    server.use(http.get('*/WebResources/op365_/missing.html', () => new HttpResponse(null, { status: 404 })));
    render(<WebResourcePage name="op365_/missing.html" />);
    expect(await screen.findByText('The web resource op365_/missing.html does not exist or you do not have access to it.')).toBeInTheDocument();
    expect(screen.queryByTitle('Web resource op365_/missing.html')).toBeNull();
  });

  it.each(['../admin', '%2e%2e/admin', '/admin', 'a\\b', 'javascript:alert(1)'])('webresource_name_%s_is_refused_without_a_request', async (name) => {
    const asked: string[] = [];
    server.use(http.get('*/*', ({ request }) => void asked.push(request.url)));
    const { container } = render(<WebResourcePage name={name} />);
    expect(await screen.findByText(`${name} is not a web resource name.`)).toBeInTheDocument();
    expect(container.querySelector('iframe')).toBeNull();
    expect(asked).toEqual([]);
  });

  it('unknown_webresource_message_in_italian', async () => {
    setUiLanguage(1040);
    server.use(http.get('*/WebResources/op365_/missing.html', () => new HttpResponse(null, { status: 404 })));
    render(<WebResourcePage name="op365_/missing.html" />);
    expect(await screen.findByText('La risorsa Web op365_/missing.html non esiste oppure non hai accesso ad essa.')).toBeInTheDocument();
  });

  it('a_server_failure_is_not_reported_as_missing', async () => {
    server.use(http.get('*/WebResources/op365_/x.html', () => new HttpResponse(null, { status: 500, statusText: 'Boom' })));
    render(<WebResourcePage name="op365_/x.html" />);
    expect(await screen.findByText(/500 Boom/)).toBeInTheDocument();
    expect(screen.queryByText(/does not exist/)).toBeNull();
  });
});

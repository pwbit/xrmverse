import { render, screen } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { server } from '../../test/server';
import { saveDevCredentials } from '../../shell/auth/devToken';
import { App } from '../../shell/App';
import { en } from '../../shell/i18n/en';

const SYSTEM_ADMIN = '627090ff-40a3-4053-8790-584edc5be201';
const USER_ID = 'aaaaaaaa-0000-0000-0000-000000000001';

const NOTICE =
  'xrmverse is an independent project and is not affiliated with, endorsed by or sponsored by Microsoft. ' +
  'Microsoft, Dataverse, Dynamics 365 and Power Apps are trademarks of the Microsoft group of companies.';

beforeEach(() => {
  sessionStorage.clear();
  saveDevCredentials({ signingKey: 'k'.repeat(40), objectId: USER_ID, name: 'Ada Admin' });
  server.use(
    http.get('*/ui/config.json', () =>
      HttpResponse.json({ authMode: 'Development', audience: 'http://localhost/', apiVersion: 'v9.2' }),
    ),
    http.get('*/api/data/v9.2/*', ({ request }) => {
      const entity = new URL(request.url).pathname.split('/').pop()!;
      if (entity === 'WhoAmI') return HttpResponse.json({ UserId: USER_ID });
      if (entity === 'systemuserrolescollection') return HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN }] });
      if (entity === 'roles') return HttpResponse.json({ value: [{ roleid: SYSTEM_ADMIN, name: 'Role', roletemplateid: SYSTEM_ADMIN }] });
      return HttpResponse.json({ value: [] });
    }),
  );
});
afterEach(() => sessionStorage.clear());

describe('AboutPage', () => {
  it('shows_the_product_name_trademark_notice_license_third_party_line_and_security_link', async () => {
    render(<App initialEntries={['/manage/about']} />);
    expect(await screen.findByRole('heading', { name: 'XRMverse' })).toBeInTheDocument();
    expect(screen.getByText(NOTICE)).toBeInTheDocument();
    expect(en['about.notice']).toBe(NOTICE);
    expect(
      screen.getByText('Third-party software notices are in THIRD_PARTY_NOTICES.md, included with the server image and the xrmd CLI.'),
    ).toBeInTheDocument();
    expect(screen.getByText(en['about.license'])).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Apache License, Version 2.0' })).toHaveAttribute(
      'href',
      'https://www.apache.org/licenses/LICENSE-2.0',
    );
    const link = screen.getByRole('link', { name: /SECURITY\.md/ });
    expect(link).toHaveAttribute('href', expect.stringMatching(/\/SECURITY\.md$/));
  });

  it('shows_the_sdk_version_from_the_health_report', async () => {
    server.use(http.get('*/admin/health', () => HttpResponse.json({ status: 'Healthy', sdkMode: 'standard', sdkVersion: '9.2.49.17970' })));
    render(<App initialEntries={['/manage/about']} />);
    expect(await screen.findByText('SDK: 9.2.49.17970')).toBeInTheDocument();
  });

  it('says_the_sdk_is_not_used_when_the_server_is_sdk_free', async () => {
    server.use(http.get('*/admin/health', () => HttpResponse.json({ status: 'Healthy', sdkMode: 'none', sdkVersion: null })));
    render(<App initialEntries={['/manage/about']} />);
    expect(await screen.findByText('SDK: not used (SDK-free server)')).toBeInTheDocument();
  });
});

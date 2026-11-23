import { render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { onPath } from '../../admin/security/testSupport';
import { Providers } from '../../renderers/classic/form/formTestSupport';
import { server } from '../../test/server';
import { mockApps } from '../testSupport';
import { DisplayZoneContext } from '../displayZone';
import { EmailStatusBanner } from './EmailStatusBanner';

const ID = 'eeeeeeee-0000-0000-0000-000000000001';

function show(record: Record<string, unknown>, onChanged = vi.fn(), pollMs = 20) {
  render(
    <Providers>
      <DisplayZoneContext.Provider value="Europe/Rome">
        <EmailStatusBanner id={ID} record={record} onChanged={onChanged} pollMs={pollMs} />
      </DisplayZoneContext.Provider>
    </Providers>,
  );
  return onChanged;
}

describe('EmailStatusBanner', () => {
  it('Pending Send: sending, and the form reads the email again once the send ends', async () => {
    mockApps();
    let status = 6;
    server.use(onPath('get', `emails(${ID})`, () => HttpResponse.json({ activityid: ID, statecode: 1, statuscode: status })));
    const onChanged = show({ statecode: 1, statuscode: 6 });
    expect(await screen.findByText(/Sending/)).toBeInTheDocument();
    expect(screen.getByText(/read-only until the send ends/)).toBeInTheDocument();
    status = 3;
    await waitFor(() => expect(onChanged).toHaveBeenCalled());
  });

  it('Sent: the time it was sent in the user zone, the sender address and the message id', async () => {
    mockApps();
    server.use(
      onPath('get', `emails(${ID})`, () =>
        HttpResponse.json({ activityid: ID, statecode: 1, statuscode: 3, senton: '2026-11-22T20:14:00Z', messageid: '<abc@example.com>', sender: 'support@example.com' }),
      ),
    );
    show({ statecode: 1, statuscode: 3 });
    // 20:14 UTC is 21:14 in Rome.
    const body = await screen.findByText(/9:14|21:14/);
    expect(body.textContent).toContain('from support@example.com');
    expect(body.textContent).toContain('Message-ID <abc@example.com>');
    expect(body.textContent).toMatch(/^Sent/);
  });

  it("Failed: the reason from the email's system job", async () => {
    mockApps();
    const filters: string[] = [];
    server.use(
      http.get('*/api/data/v9.2/asyncoperations', ({ request }) => {
        filters.push(new URL(request.url).searchParams.get('$filter') ?? '');
        return HttpResponse.json({ value: [{ asyncoperationid: 'j1', message: 'The SMTP server refused the login of support@example.com.' }] });
      }),
    );
    show({ statecode: 0, statuscode: 8 });
    expect(await screen.findByText(/The SMTP server refused the login of support@example.com./)).toBeInTheDocument();
    expect(screen.getByText(/Not sent/)).toBeInTheDocument();
    expect(filters[0]).toBe(`regardingobjectid eq ${ID} and operationtype eq 99011`);
  });

  it('a draft shows nothing', () => {
    mockApps();
    const { container } = render(
      <Providers>
        <EmailStatusBanner id={ID} record={{ statecode: 0, statuscode: 1 }} onChanged={() => {}} />
      </Providers>,
    );
    expect(container.querySelector('.fui-MessageBar')).toBeNull();
  });
});

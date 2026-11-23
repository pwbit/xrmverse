import { http, HttpResponse } from 'msw';
import { describe, expect, it, vi } from 'vitest';
import { onPath } from '../../admin/security/testSupport';
import { attr, entity } from '../../admin/tables/testAttrs';
import { XrmApi } from '../../shell/api/client';
import { server } from '../../test/server';
import type { RecordContext } from '../record/loadRecord';
import { emailSendCommands, sendEmail, unresolvedRecipients } from './emailCommands';

const ID = 'eeeeeeee-0000-0000-0000-000000000001';
const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });

function ctxOf(record: Record<string, unknown> | undefined, over: { table?: string; rights?: RecordContext['rights'] } = {}): RecordContext {
  return {
    entity: entity([attr('subject')], { logicalName: over.table ?? 'email', entitySetName: over.table === 'task' ? 'tasks' : 'emails' }),
    fieldContext: { table: over.table ?? 'email', lookups: [], globalChoices: {}, record: record ? { set: 'emails', id: ID, data: record } : undefined },
    record,
    rights: over.rights ?? ['Read', 'Write'],
    canCreate: true,
  };
}

describe('emailSendCommands', () => {
  const onSend = () => {};
  it('offers Send on an open email the user may write', () => {
    const [send] = emailSendCommands({ ctx: ctxOf({ statecode: 0 }), record: { statecode: 0 }, busy: false, onSend });
    expect(send).toMatchObject({ id: 'email:send', label: 'Send', disabled: false });
  });

  it('is hidden on a new email, a closed one, without Write, and on other tables', () => {
    expect(emailSendCommands({ ctx: ctxOf(undefined), record: undefined, busy: false, onSend })).toEqual([]);
    expect(emailSendCommands({ ctx: ctxOf({ statecode: 1 }), record: { statecode: 1 }, busy: false, onSend })).toEqual([]);
    expect(emailSendCommands({ ctx: ctxOf({ statecode: 0 }, { rights: ['Read'] }), record: { statecode: 0 }, busy: false, onSend })).toEqual([]);
    expect(emailSendCommands({ ctx: ctxOf({ statecode: 0 }, { table: 'task' }), record: { statecode: 0 }, busy: false, onSend })).toEqual([]);
  });

  it('follows the record as the form last read it (a send in progress hides it)', () => {
    expect(emailSendCommands({ ctx: ctxOf({ statecode: 0 }), record: { statecode: 1, statuscode: 6 }, busy: false, onSend })).toEqual([]);
    expect(emailSendCommands({ ctx: ctxOf({ statecode: 0 }), record: { statecode: 0 }, busy: true, onSend })[0]!.disabled).toBe(true);
  });
});

describe('unresolvedRecipients', () => {
  it('lists the addresses of To, Cc and Bcc that name no record', () => {
    expect(
      unresolvedRecipients({
        from: [{ address: 'me@example.com' }],
        to: [{ id: 'c1', logicalName: 'contact' }, { address: 'a@example.com' }],
        cc: [{ address: 'b@example.com' }],
        bcc: null,
      }),
    ).toEqual(['a@example.com', 'b@example.com']);
  });
});

describe('sendEmail', () => {
  function deps(over: Partial<Parameters<typeof sendEmail>[0]> = {}) {
    const calls: string[] = [];
    const notes: string[] = [];
    return {
      calls,
      notes,
      deps: {
        api: api(),
        id: ID,
        save: vi.fn(async () => {
          calls.push('save');
          return true;
        }),
        values: () => ({ to: [{ id: 'c1', logicalName: 'contact' }] }),
        notify: (text: string) => notes.push(text),
        clear: () => notes.push('(cleared)'),
        reload: () => calls.push('reload'),
        ...over,
      },
    };
  }

  function mockSend(response: () => Response, allowUnresolved = false) {
    const bodies: unknown[] = [];
    server.use(
      http.get('*/api/data/v9.2/organizations', () => HttpResponse.json({ value: [{ allowunresolvedpartiesonemailsend: allowUnresolved }] })),
      onPath('post', `emails(${ID})/Microsoft.Dynamics.CRM.SendEmail`, async ({ request }) => {
        bodies.push(await request.json());
        return response();
      }),
    );
    return bodies;
  }

  it('saves the form first, then sends with IssueSend true and reads the email again', async () => {
    const bodies = mockSend(() => HttpResponse.json({ Subject: 'Hi' }));
    const { deps: d, calls } = deps();
    expect(await sendEmail(d)).toBe(true);
    expect(calls).toEqual(['save', 'reload']);
    expect(bodies).toEqual([{ IssueSend: true }]);
  });

  it('does not send when the save fails', async () => {
    const bodies = mockSend(() => HttpResponse.json({}));
    const { deps: d } = deps({ save: async () => false });
    expect(await sendEmail(d)).toBe(false);
    expect(bodies).toEqual([]);
  });

  it("shows the server's message in the form notification on a 400", async () => {
    mockSend(() => HttpResponse.json({ error: { code: '0x80040203', message: 'Only a queue can send in this environment.' } }, { status: 400 }));
    const { deps: d, notes, calls } = deps();
    expect(await sendEmail(d)).toBe(false);
    expect(notes.at(-1)).toContain('Only a queue can send in this environment.');
    expect(calls).toEqual(['save']);
  });

  it('warns before sending to unresolved recipients when the organization refuses them', async () => {
    const bodies = mockSend(() => HttpResponse.json({}));
    const { deps: d, notes } = deps({ values: () => ({ to: [{ address: 'x@example.com' }] }) });
    expect(await sendEmail(d)).toBe(false);
    expect(bodies).toEqual([]);
    expect(notes.at(-1)).toContain('x@example.com');
  });

  it('sends to unresolved recipients when the organization allows them', async () => {
    const bodies = mockSend(() => HttpResponse.json({}), true);
    const { deps: d } = deps({ values: () => ({ to: [{ address: 'x@example.com' }] }) });
    expect(await sendEmail(d)).toBe(true);
    expect(bodies).toHaveLength(1);
  });

  it('waits for a save already running (an auto-save) before its own save', async () => {
    const bodies = mockSend(() => HttpResponse.json({ Subject: 'Hi' }));
    let running = true;
    setTimeout(() => (running = false), 150);
    const { deps: d, calls } = deps({ saving: () => running });
    expect(await sendEmail(d)).toBe(true);
    expect(calls).toEqual(['save', 'reload']);
    expect(bodies).toHaveLength(1);
  });

  it('says so, instead of stopping silently, when that save does not end', async () => {
    const bodies = mockSend(() => HttpResponse.json({}));
    const { deps: d, calls, notes } = deps({ saving: () => true, savingTimeoutMs: 200 });
    expect(await sendEmail(d)).toBe(false);
    expect(calls).toEqual([]);
    expect(bodies).toEqual([]);
    expect(notes.at(-1)).toMatch(/still being saved/);
  });
});

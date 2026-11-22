import { http, HttpResponse } from 'msw';
import { describe, expect, it } from 'vitest';
import { XrmApi } from '../shell/api/client';
import { server } from '../test/server';
import { canReadWorkflows } from './useCanReadWorkflows';

const api = () => new XrmApi({ getToken: async () => 'tok', onUnauthorized: () => {} });

function answer(...statuses: number[]) {
  let asked = 0;
  server.use(
    http.get('*/authoring/workflows', () => {
      const status = statuses[Math.min(asked++, statuses.length - 1)]!;
      return status === 200 ? HttpResponse.json([]) : HttpResponse.json({ error: { message: 'no' } }, { status });
    }),
  );
  return () => asked;
}

describe('canReadWorkflows (W7)', () => {
  it('keeps a success for the session', async () => {
    const asked = answer(200);
    const a = api();
    expect(await canReadWorkflows(a)).toBe(true);
    expect(await canReadWorkflows(a)).toBe(true);
    expect(asked()).toBe(1);
  });

  it('keeps a 403 as false for the session', async () => {
    const asked = answer(403, 200);
    const a = api();
    expect(await canReadWorkflows(a)).toBe(false);
    expect(await canReadWorkflows(a)).toBe(false);
    expect(asked()).toBe(1);
  });

  it('keeps a 401 as false', async () => {
    const asked = answer(401, 200);
    const a = api();
    expect(await canReadWorkflows(a)).toBe(false);
    expect(await canReadWorkflows(a)).toBe(false);
    expect(asked()).toBe(1);
  });

  it('does not keep another failure: a later visit asks again', async () => {
    const asked = answer(500, 501, 200);
    const a = api();
    expect(await canReadWorkflows(a)).toBe(false);
    expect(await canReadWorkflows(a)).toBe(false);
    expect(await canReadWorkflows(a)).toBe(true);
    expect(await canReadWorkflows(a)).toBe(true);
    expect(asked()).toBe(3);
  });

  it('does not keep a network error', async () => {
    let asked = 0;
    server.use(
      http.get('*/authoring/workflows', () => {
        asked++;
        return asked === 1 ? HttpResponse.error() : HttpResponse.json([]);
      }),
    );
    const a = api();
    expect(await canReadWorkflows(a)).toBe(false);
    expect(await canReadWorkflows(a)).toBe(true);
    expect(asked).toBe(2);
  });

  it('a new session asks again', async () => {
    const asked = answer(403, 200);
    expect(await canReadWorkflows(api())).toBe(false);
    expect(await canReadWorkflows(api())).toBe(true);
    expect(asked()).toBe(2);
  });
});

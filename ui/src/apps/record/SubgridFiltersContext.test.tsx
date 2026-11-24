import { render, screen, waitFor } from '@testing-library/react';
import { http, HttpResponse } from 'msw';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { server } from '../../test/server';
import { SubgridFiltersProvider, useSubgridFilters, useSubgridFiltersStatus } from './SubgridFiltersContext';

const resource = (source: string) => ({ forms: { '{F}': { sub: { source, target: 'parentid' } } } });

function Probe({ formId }: { formId: string }) {
  const entries = useSubgridFilters(formId);
  return <p>{entries ? JSON.stringify(entries) : 'none'}</p>;
}

function StatusProbe() {
  return <p>status:{useSubgridFiltersStatus()}</p>;
}

afterEach(() => vi.restoreAllMocks());

describe('SubgridFiltersProvider', () => {
  it('renders at once and merges the resources in name order, the first winning', async () => {
    server.use(
      http.get('*/WebResources/a.json', () => HttpResponse.json(resource('a_col'))),
      http.get('*/WebResources/b.json', () => HttpResponse.json(resource('b_col'))),
    );
    render(
      <SubgridFiltersProvider names={['a.json', 'b.json']}>
        <p>child</p>
        <Probe formId="{F}" />
      </SubgridFiltersProvider>,
    );
    expect(screen.getByText('child')).toBeTruthy();
    await waitFor(() => expect(screen.getByText(/a_col/)).toBeTruthy());
    expect(screen.queryByText(/b_col/)).toBeNull();
  });

  it('skips a missing resource with one warning and applies the others', async () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    server.use(
      http.get('*/WebResources/gone.json', () => new HttpResponse(null, { status: 404 })),
      http.get('*/WebResources/b.json', () => HttpResponse.json(resource('b_col'))),
    );
    render(
      <SubgridFiltersProvider names={['gone.json', 'b.json']}>
        <Probe formId="f" />
      </SubgridFiltersProvider>,
    );
    await waitFor(() => expect(screen.getByText(/b_col/)).toBeTruthy());
    expect(warn).toHaveBeenCalledTimes(1);
    expect(warn.mock.calls[0]![0]).toBe('[xrmverse subgrid filter]');
  });

  it('gives undefined for a form that is not configured', async () => {
    server.use(http.get('*/WebResources/a.json', () => HttpResponse.json(resource('a_col'))));
    render(
      <SubgridFiltersProvider names={['a.json']}>
        <Probe formId="{OTHER}" />
      </SubgridFiltersProvider>,
    );
    await new Promise((r) => setTimeout(r, 50));
    expect(screen.getByText('none')).toBeTruthy();
  });

  it('loads once per distinct names list', async () => {
    let calls = 0;
    server.use(http.get('*/WebResources/a.json', () => (calls++, HttpResponse.json(resource('a_col')))));
    const { rerender } = render(
      <SubgridFiltersProvider names={['a.json']}>
        <Probe formId="F" />
      </SubgridFiltersProvider>,
    );
    await waitFor(() => expect(screen.getByText(/a_col/)).toBeTruthy());
    rerender(
      <SubgridFiltersProvider names={['a.json']}>
        <Probe formId="F" />
      </SubgridFiltersProvider>,
    );
    expect(calls).toBe(1);
  });

  it('is pending until every resource is read, then ready', async () => {
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    server.use(http.get('*/WebResources/a.json', async () => (await gate, HttpResponse.json(resource('a_col')))));
    render(
      <SubgridFiltersProvider names={['a.json']}>
        <StatusProbe />
      </SubgridFiltersProvider>,
    );
    expect(screen.getByText('status:pending')).toBeTruthy();
    release();
    await waitFor(() => expect(screen.getByText('status:ready')).toBeTruthy());
  });

  it('is ready at once without resources', () => {
    render(
      <SubgridFiltersProvider names={[]}>
        <StatusProbe />
      </SubgridFiltersProvider>,
    );
    expect(screen.getByText('status:ready')).toBeTruthy();
  });

  it('is failed when a resource could not be read, and still applies the others', async () => {
    vi.spyOn(console, 'warn').mockImplementation(() => {});
    server.use(
      http.get('*/WebResources/gone.json', () => new HttpResponse(null, { status: 500 })),
      http.get('*/WebResources/b.json', () => HttpResponse.json(resource('b_col'))),
    );
    render(
      <SubgridFiltersProvider names={['gone.json', 'b.json']}>
        <StatusProbe />
        <Probe formId="f" />
      </SubgridFiltersProvider>,
    );
    await waitFor(() => expect(screen.getByText('status:failed')).toBeTruthy());
    expect(screen.getByText(/b_col/)).toBeTruthy();
  });
});

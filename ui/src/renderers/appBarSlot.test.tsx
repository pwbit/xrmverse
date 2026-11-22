import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { classicRenderer } from './classic';
import { modernRenderer } from './modern';

vi.mock('../shell/auth/AuthProvider', () => ({ useAuth: () => ({ sessionCookieProblem: null, signOut: () => {}, user: null }) }));

describe.each([
  ['classic', classicRenderer],
  ['modern', modernRenderer],
])('%s app frame appBar slot', (_name, renderer) => {
  it('renders the appBar before the page', () => {
    const { AppFrame } = renderer;
    render(
      <MemoryRouter>
        <AppFrame appName="Sales" siteMap={{ areas: [] } as never} lcid={1033} appId="a" current={{} as never} appBar={<div>the bar</div>}>
          <div>the page</div>
        </AppFrame>
      </MemoryRouter>,
    );
    expect(screen.getByText('the bar')).toBeInTheDocument();
    expect(screen.getByText('the bar').compareDocumentPosition(screen.getByText('the page')) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it('renders sidePanes as a sibling after the main area', () => {
    const { AppFrame } = renderer;
    render(
      <MemoryRouter>
        <AppFrame appName="Sales" siteMap={{ areas: [] } as never} lcid={1033} appId="a" current={{} as never} sidePanes={<div>the panes</div>}>
          <div>the page</div>
        </AppFrame>
      </MemoryRouter>,
    );
    const main = screen.getByRole('main');
    expect(main.nextElementSibling).toBe(screen.getByText('the panes'));
  });
});

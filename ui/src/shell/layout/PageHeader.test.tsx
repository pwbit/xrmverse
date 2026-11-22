import { render, screen, within } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it } from 'vitest';
import { en } from '../i18n/en';
import { PageHeader } from './PageHeader';

const renderAt = (node: React.ReactNode) =>
  render(<MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{node}</MemoryRouter>);

describe('PageHeader', () => {
  it('Breadcrumb_link_and_title_are_separate', () => {
    renderAt(<PageHeader title="Account (account)" breadcrumb={[{ label: 'Tables', to: '/manage/tables' }]} />);
    const crumbs = screen.getByRole('navigation', { name: en['breadcrumb.label'] });
    const link = within(crumbs).getByRole('link', { name: 'Tables' });
    expect(link).toHaveAttribute('href', '/manage/tables');
    const heading = screen.getByRole('heading', { level: 2 });
    expect(heading.textContent).toBe('Account (account)');
    expect(heading.contains(link)).toBe(false);
  });

  it('Title_only_has_no_breadcrumb', () => {
    renderAt(<PageHeader title="Tables" actions={<button>New table</button>} />);
    expect(screen.queryByRole('navigation')).toBeNull();
    const heading = screen.getByRole('heading', { level: 2, name: 'Tables' });
    expect(heading.contains(screen.getByRole('button', { name: 'New table' }))).toBe(false);
  });

  it('Actions_sit_in_a_named_command_bar_below_the_title', () => {
    renderAt(<PageHeader title="Tables" actions={<button>New table</button>} />);
    const bar = screen.getByRole('toolbar', { name: 'Tables commands' });
    expect(within(bar).getByRole('button', { name: 'New table' })).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 2 }).compareDocumentPosition(bar) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });
});

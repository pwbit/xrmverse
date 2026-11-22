import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { parsePresentation } from '../../parsers/presentationJson';
import { CHOICE_TEXT_LIGHT } from '../../shell/theme';
import { ChoiceBadge, ChoiceBadges } from './ChoiceBadge';
import { PresentationProvider } from './PresentationContext';

const GREEN = '#107C10';

describe('ChoiceBadge', () => {
  it('shows the label on the given background and text colours, with no role', () => {
    render(<ChoiceBadge label="High" background={GREEN} text={CHOICE_TEXT_LIGHT} />);
    const badge = screen.getByText('High');
    expect(badge).toHaveClass('fui-Badge');
    expect(badge).toHaveStyle({ backgroundColor: GREEN, color: CHOICE_TEXT_LIGHT });
    expect(badge).not.toHaveAttribute('role');
  });

  it('is smaller with the compact density', () => {
    const { model } = parsePresentation(JSON.stringify({ version: 1, theme: { density: 'compact' } }));
    const { unmount } = render(<ChoiceBadge label="High" background={GREEN} text={CHOICE_TEXT_LIGHT} />);
    const comfortable = screen.getByText('High').className;
    unmount();
    render(
      <PresentationProvider model={model}>
        <ChoiceBadge label="High" background={GREEN} text={CHOICE_TEXT_LIGHT} />
      </PresentationProvider>,
    );
    expect(screen.getByText('High').className).not.toBe(comfortable);
  });

  it('ChoiceBadges shows one badge per value, in order', () => {
    render(
      <ChoiceBadges
        badges={[
          { label: 'Red', background: GREEN, text: CHOICE_TEXT_LIGHT },
          { label: 'Green', background: GREEN, text: CHOICE_TEXT_LIGHT },
        ]}
      />,
    );
    expect([...document.querySelectorAll('.fui-Badge')].map((b) => b.textContent)).toEqual(['Red', 'Green']);
  });
});

import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

/** The "Show as" menu button of a view or a subgrid (it names the current mode). */
export const showAs = () => screen.findByRole('button', { name: /^Show as: / });
export const queryShowAs = () => screen.queryByRole('button', { name: /^Show as: / });

/** The modes the "Show as" menu offers, in order (the menu is closed again). */
export async function modeNames(): Promise<(string | null)[]> {
  await userEvent.click(await showAs());
  const names = (await screen.findAllByRole('menuitemradio')).map((item) => item.textContent);
  await userEvent.keyboard('{Escape}');
  return names;
}

/** Picks a mode in the "Show as" menu. */
export async function pickMode(name: string): Promise<void> {
  await userEvent.click(await showAs());
  await userEvent.click(await screen.findByRole('menuitemradio', { name }));
}

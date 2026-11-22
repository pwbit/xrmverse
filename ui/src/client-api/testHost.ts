import type { LibraryLoader } from './scriptHost';

/** A test loader: the library source comes from `sources` by name, a missing one is a 404, and it evaluates in the host window. */
export const sourceLoader =
  (sources: Record<string, string>): LibraryLoader =>
  async (win, _url, name) => {
    const source = sources[name];
    if (source === undefined) throw new Error('404');
    (win as unknown as { eval(s: string): void }).eval(source);
  };

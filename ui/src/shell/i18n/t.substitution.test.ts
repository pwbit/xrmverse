import { t } from './t';

vi.mock('./en', () => ({ en: { 'x.greet': 'Hi {name}, {n} left, {missing}' } }));

describe('t substitution', () => {
  it('substitutes known placeholders and leaves unknown ones as written', () => {
    const key = 'x.greet' as unknown as Parameters<typeof t>[0];
    expect(t(key, { name: 'Ann', n: 3 })).toBe('Hi Ann, 3 left, {missing}');
  });
});

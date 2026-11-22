import { describe, expect, it } from 'vitest';
import { errorText, XrmError } from './errors';

describe('errorText', () => {
  it('shows the server message verbatim, without the error code', () => {
    const e = new XrmError(403, 'denied: you lack prvWriteAccount', '0x80040220');
    expect(e.message).toBe('0x80040220: denied: you lack prvWriteAccount');
    expect(errorText(e)).toBe('denied: you lack prvWriteAccount');
    expect(errorText(new XrmError(500, 'Internal error'))).toBe('Internal error');
  });

  it('shows the message of another error, and any other value as text', () => {
    expect(errorText(new TypeError('Failed to fetch'))).toBe('Failed to fetch');
    expect(errorText('offline')).toBe('offline');
  });
});

/** A failed Web API or Admin API call: `{ "error": { "code": "0x80040220", "message": "..." } }` on the server side. */
export class XrmError extends Error {
  readonly status: number;
  /** The Dataverse error code, e.g. `0x80040220`; absent when the response carried no error envelope. */
  readonly code?: string;
  readonly serverMessage: string;

  constructor(status: number, serverMessage: string, code?: string) {
    super(code ? `${code}: ${serverMessage}` : serverMessage);
    this.name = 'XrmError';
    this.status = status;
    this.code = code;
    this.serverMessage = serverMessage;
  }
}

/**
 * The text to show for a failure: the server's own message, verbatim and without the error code, for an {@link XrmError};
 * the message of any other error; the value as text otherwise.
 */
export function errorText(e: unknown): string {
  if (e instanceof XrmError) return e.serverMessage;
  return e instanceof Error ? e.message : String(e);
}

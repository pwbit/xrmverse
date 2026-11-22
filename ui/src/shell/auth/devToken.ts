import { t } from '../i18n/t';

const ISSUER = 'xrmverse-dev';
const LIFETIME_SECONDS = 3600;

export interface DevTokenArgs {
  signingKey: string;
  objectId: string;
  name: string;
  audience: string;
  now?: Date;
}

function base64Url(bytes: Uint8Array): string {
  let binary = '';
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

const encode = (text: string) => new TextEncoder().encode(text);

/**
 * Mints the same HS256 token as `docker/dev-token.sh` and the CLI's dev mode: header `{alg,typ}`, payload
 * `{iss,aud,oid,name,nbf,exp}` in that order (compact JSON), base64url without padding, whole seconds.
 */
export async function createDevToken(args: DevTokenArgs): Promise<string> {
  const subtle = globalThis.crypto?.subtle;
  if (!subtle) throw new Error(t('auth.dev.noWebCrypto'));
  const nbf = Math.floor((args.now ?? new Date()).getTime() / 1000);
  const header = base64Url(encode(JSON.stringify({ alg: 'HS256', typ: 'JWT' })));
  const payload = base64Url(
    encode(
      JSON.stringify({
        iss: ISSUER,
        aud: args.audience,
        oid: args.objectId,
        name: args.name,
        nbf,
        exp: nbf + LIFETIME_SECONDS,
      }),
    ),
  );
  const key = await subtle.importKey('raw', encode(args.signingKey), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const signature = await subtle.sign('HMAC', key, encode(`${header}.${payload}`));
  return `${header}.${payload}.${base64Url(new Uint8Array(signature))}`;
}

const STORAGE_KEY = 'xrmverse.devSignIn';

export interface DevCredentials {
  signingKey: string;
  objectId: string;
  name: string;
}

/** The dev signing key and object id live in `sessionStorage` only (gone when the tab closes), never `localStorage`. */
export function loadDevCredentials(): DevCredentials | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    if (!raw) return null;
    const c = JSON.parse(raw) as Partial<DevCredentials>;
    return c.signingKey && c.objectId ? { signingKey: c.signingKey, objectId: c.objectId, name: c.name || t('auth.dev.defaultName') } : null;
  } catch {
    return null;
  }
}

export function saveDevCredentials(credentials: DevCredentials): void {
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(credentials));
  } catch {
    // Storage unavailable: the credentials last for this page load only.
  }
}

export function clearDevCredentials(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing stored.
  }
}

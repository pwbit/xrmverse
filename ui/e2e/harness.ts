import { spawn } from 'node:child_process';
import { createHmac, randomBytes } from 'node:crypto';
import { chmodSync, existsSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

/** Shared by the global setup, the global teardown and the specs: where things are and what one run started. */
export const uiDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
export const repoDir = resolve(uiDir, '..');

/** The same image as the Testcontainers fixture in tests/Xrmd.Integration.Tests/TestDatabase.cs. */
export const SQL_IMAGE = 'mcr.microsoft.com/mssql/server:2022-latest';

/** The seed administrator's object id (Seed:AdminObjectId), fixed so the specs can sign in as it. */
export const ADMIN_OBJECT_ID = '00000000-0000-0000-0000-0000000e2e01';

/** What a run started; written by the global setup and read by the teardown (and by the next setup after a crash). */
export interface RunState {
  baseURL: string;
  signingKey: string;
  container?: string;
  hostPid?: number;
  /** The second host, in Local mode over https (see global-setup). */
  localHostPid?: number;
  /** Hosts a spec started for an organization of its own (startExtraOrganization); the teardown stops them too. */
  extraHostPids?: number[];
  solutionZip: string;
  workDir: string;
}

const statePath = resolve(uiDir, 'e2e', '.run-state.json');

/**
 * The state holds the run's throwaway dev signing key in plaintext: the file is git-ignored, removed at teardown, and
 * readable by its owner only (the mode is set again in case the file already existed).
 */
export function writeState(state: RunState): void {
  writeFileSync(statePath, JSON.stringify(state, null, 2), { mode: 0o600 });
  chmodSync(statePath, 0o600);
}

export function readState(): RunState | undefined {
  return existsSync(statePath) ? (JSON.parse(readFileSync(statePath, 'utf8')) as RunState) : undefined;
}

export function clearState(): void {
  rmSync(statePath, { force: true });
}

/** A short random suffix that keeps names unique per run. */
export function uniqueSuffix(): string {
  return randomBytes(4).toString('hex');
}

/**
 * The dev-mode HS256 token the CLI and the shell's DevSignIn mint (iss xrmverse-dev, aud = Auth:Audience, oid,
 * name, 3600 s), for the specs' own Web API checks and setup.
 */
export function mintDevToken(signingKey: string, audience: string, objectId: string, name: string): string {
  const b64 = (o: object) => Buffer.from(JSON.stringify(o)).toString('base64url');
  const nbf = Math.floor(Date.now() / 1000);
  const unsigned = `${b64({ alg: 'HS256', typ: 'JWT' })}.${b64({ iss: 'xrmverse-dev', aud: audience, oid: objectId, name, nbf, exp: nbf + 3600 })}`;
  return `${unsigned}.${createHmac('sha256', signingKey).update(unsigned).digest('base64url')}`;
}

/** The Local-mode host's address and the administrator's local credential, for the specs of the local identity provider. */
export function localRunEnv(): { baseURL: string; userName: string; password: string } {
  const baseURL = process.env.XRMD_E2E_LOCAL_BASE_URL;
  const userName = process.env.XRMD_E2E_LOCAL_USER;
  const password = process.env.XRMD_E2E_LOCAL_PASSWORD;
  if (!baseURL || !userName || !password) throw new Error('The E2E global setup did not start the Local-mode host (XRMD_E2E_LOCAL_* are not set).');
  return { baseURL, userName, password };
}

/** The run's values for the specs (the global setup exports them as environment variables). */
export function runEnv(): { baseURL: string; signingKey: string; solutionZip: string } {
  const baseURL = process.env.XRMD_E2E_BASE_URL;
  const signingKey = process.env.XRMD_E2E_SIGNING_KEY;
  const solutionZip = process.env.XRMD_E2E_SOLUTION_ZIP;
  if (!baseURL || !signingKey || !solutionZip) throw new Error('The E2E global setup did not run (XRMD_E2E_* are not set).');
  return { baseURL, signingKey, solutionZip };
}

function freePort(): Promise<number> {
  return new Promise((ok, fail) => {
    const server = createServer();
    server.once('error', fail);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      server.close(() => (typeof address === 'object' && address ? ok(address.port) : fail(new Error('No port.'))));
    });
  });
}

/**
 * Another organization for a spec (one database and one host per organization, as `xrmd server org add` sets it up): a
 * Development-mode host on the run's SQL Server, with the run's dev signing key and the same administrator object id, on the
 * database `database`. It answers /healthz before this returns; `stop` ends it (the teardown does too, after a crash).
 * The host writes its output to e2e/.host-{database}.log (git-ignored with the other host logs).
 */
export async function startExtraOrganization(database: string, orgName: string): Promise<{ baseURL: string; stop: () => void }> {
  const sqlServer = process.env.XRMD_E2E_SQL_SERVER;
  const sdk = process.env.XRMD_E2E_SDK_DIR;
  const { signingKey } = runEnv();
  if (!sqlServer || !sdk) throw new Error('The E2E global setup did not export XRMD_E2E_SQL_SERVER and XRMD_E2E_SDK_DIR.');
  const state = readState();
  if (!state) throw new Error('The E2E run state is missing.');
  const baseURL = `http://127.0.0.1:${await freePort()}`;
  const logPath = resolve(uiDir, 'e2e', `.host-${database}.log`);
  const log = openSync(logPath, 'w');
  const host = spawn('dotnet', ['run', '--project', 'src/Xrmd.Host', '--no-build', '--no-launch-profile'], {
    cwd: repoDir,
    detached: true,
    stdio: ['ignore', log, log],
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      XRMD_SDK_DIR: sdk,
      ASPNETCORE_URLS: baseURL,
      ConnectionStrings__Org: `${sqlServer};Database=${database}`,
      Auth__Mode: 'Development',
      Auth__Audience: `${baseURL}/`,
      Auth__DevSigningKey: signingKey,
      Seed__OrgName: orgName,
      Seed__AdminObjectId: ADMIN_OBJECT_ID,
      Seed__AdminName: 'Administrator',
      Ui__RootPath: join(uiDir, 'dist'),
      Files__FileSystem__Root: join(state.workDir, `files-${database}`),
    },
  });
  host.unref();
  const pid = host.pid!;
  writeState({ ...state, extraHostPids: [...(state.extraHostPids ?? []), pid] });
  const stop = () => {
    try {
      process.kill(-pid, 'SIGTERM');
    } catch {
      // already gone
    }
  };

  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline) {
    try {
      process.kill(pid, 0);
    } catch {
      throw new Error(`The host of ${database} exited during startup; see ${logPath}.`);
    }
    try {
      if ((await fetch(`${baseURL}/healthz`, { signal: AbortSignal.timeout(5000) })).ok) return { baseURL, stop };
    } catch {
      // not listening yet
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  stop();
  throw new Error(`The host of ${database} did not answer /healthz within 240 s; see ${logPath}.`);
}

import { execFileSync, spawn } from 'node:child_process';
import { request as httpsRequest } from 'node:https';
import { randomBytes } from 'node:crypto';
import { cpSync, existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { stopRun } from './global-teardown';
import { ADMIN_OBJECT_ID, readState, repoDir, SQL_IMAGE, uiDir, uniqueSuffix, writeState, type RunState } from './harness';

/** The host's output, kept after the run (git-ignored) so a failed start or scenario can be diagnosed. */
const hostLog = resolve(uiDir, 'e2e', '.host.log');
const localHostLog = resolve(uiDir, 'e2e', '.host-local.log');
const fixtureDir = resolve(repoDir, 'tests', 'fixtures', 'solutions', 'XrmverseFixture');
const testPluginsDll = resolve(repoDir, 'tests', 'Xrmd.TestPlugins', 'bin', 'Debug', 'net462', 'Xrmd.TestPlugins.dll');

/** The pinned Microsoft SDK package version, read from its single source (Xrmd.Abstractions.Sdk.SdkPackage.Version). */
function sdkPackageVersion(): string {
  const source = readFileSync(resolve(repoDir, 'src', 'Xrmd.Abstractions', 'Sdk', 'SdkPackage.cs'), 'utf8');
  const version = /\bconst string Version = "([^"]+)"/.exec(source)?.[1];
  if (!version) throw new Error('SdkPackage.Version not found in src/Xrmd.Abstractions/Sdk/SdkPackage.cs.');
  return version;
}

/**
 * The SDK folder of the host (Plan 12): the restored package in the NuGet cache (NUGET_PACKAGES, else ~/.nuget/packages),
 * as the .NET test helpers find it. The host is built without the SDK, and a developer restores the package to compile.
 */
function sdkDir(): string {
  const packages = process.env.NUGET_PACKAGES || join(homedir(), '.nuget', 'packages');
  const version = sdkPackageVersion();
  const dir = join(packages, 'microsoft.powerplatform.dataverse.client', version, 'lib', 'net10.0');
  if (!existsSync(join(dir, 'Microsoft.Xrm.Sdk.dll'))) {
    throw new Error(
      `The Microsoft SDK was not found in ${dir}: the host needs it (XRMD_SDK_DIR). ` +
        `Restore the solution first ('dotnet restore xrmverse.sln'), which puts Microsoft.PowerPlatform.Dataverse.Client ` +
        `${version} in the NuGet cache.`,
    );
  }
  return dir;
}

/** Values never written to an error message or log: the run's SA password and dev signing key. */
const secrets: string[] = [];

function redact(text: string): string {
  return secrets.reduce((out, secret) => (secret ? out.split(secret).join('***') : out), text);
}

/** Runs a command; a secret must reach it through `env`, never `args`, and is redacted from the error anyway. */
function run(cmd: string, args: string[], cwd = repoDir, env?: NodeJS.ProcessEnv): string {
  try {
    return execFileSync(cmd, args, { cwd, env: env && { ...process.env, ...env }, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (e) {
    const err = e as { stdout?: string; stderr?: string; message: string };
    throw new Error(redact(`${cmd} ${args.join(' ')} failed: ${err.message}\n${err.stdout ?? ''}\n${err.stderr ?? ''}`));
  }
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
 * The Plan 5 fixture solution as a zip, built the way tests/Shared/SolutionFixtureZip.cs builds it: every file of
 * the fixture folder plus the net462 Xrmd.TestPlugins.dll at each path that a PluginAssembly/FileName names.
 */
function buildSolutionZip(workDir: string): string {
  run('dotnet', ['build', 'tests/Xrmd.TestPlugins', '-c', 'Debug', '-nologo', '-v', 'q']);
  const staging = join(workDir, 'XrmverseFixture');
  cpSync(fixtureDir, staging, { recursive: true });
  const customizations = readFileSync(join(fixtureDir, 'customizations.xml'), 'utf8');
  const fileNames = [...customizations.matchAll(/<PluginAssembly\b[\s\S]*?<FileName>([^<]+)<\/FileName>/g)].map((m) => m[1]);
  if (fileNames.length === 0) throw new Error('The fixture customizations.xml names no PluginAssembly/FileName.');
  for (const name of fileNames) {
    const target = join(staging, name.replace(/^\/+/, ''));
    mkdirSync(dirname(target), { recursive: true });
    cpSync(testPluginsDll, target);
  }
  const zip = join(workDir, 'XrmverseFixture.zip');
  run('zip', ['-r', '-X', '-q', zip, '.'], staging);
  return zip;
}

/** GET /healthz of the https host, which has a self-signed certificate (the E2E trusts it, like the browser's ignoreHTTPSErrors). */
function httpsHealthy(baseURL: string): Promise<boolean> {
  return new Promise((ok) => {
    const req = httpsRequest(`${baseURL}/healthz`, { rejectUnauthorized: false, timeout: 5000 }, (res) => {
      res.resume();
      ok(res.statusCode === 200);
    });
    req.on('error', () => ok(false));
    req.on('timeout', () => req.destroy());
    req.end();
  });
}

async function waitForHealth(baseURL: string, pid: number, log = hostLog): Promise<void> {
  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline) {
    try {
      process.kill(pid, 0);
    } catch {
      throw new Error(redact(`The host exited during startup; see ${log}:\n${readFileSync(log, 'utf8').slice(-4000)}`));
    }
    try {
      if (baseURL.startsWith('https:')) {
        if (await httpsHealthy(baseURL)) return;
      } else {
        const res = await fetch(`${baseURL}/healthz`, { signal: AbortSignal.timeout(5000) });
        if (res.ok) return;
      }
    } catch {
      // not listening yet, or no answer within 5 s
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`The host did not answer /healthz within 240 s; see ${log}.`);
}

/**
 * A second host in Local mode (the local identity provider, plan 2.8) over https with a throwaway self-signed certificate
 * (the specs use ignoreHTTPSErrors), on its own database of the same SQL Server. Its administrator gets a local credential
 * from the one-shot password file, as `xrmd server init --auth local` arranges it.
 */
async function startLocalHost(state: RunState, workDir: string, sdk: string, sqlPort: string, saPassword: string): Promise<void> {
  const certDir = join(workDir, 'local-tls');
  mkdirSync(certDir, { recursive: true });
  const cert = join(certDir, 'cert.pem');
  const key = join(certDir, 'key.pem');
  run('openssl', ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', key, '-out', cert, '-days', '2', '-subj', '/CN=localhost',
    '-addext', 'subjectAltName=DNS:localhost']);

  const secretsDir = join(workDir, 'secrets');
  mkdirSync(secretsDir, { recursive: true, mode: 0o700 });
  const userName = 'e2e-local-admin';
  const password = `E2e-${randomBytes(12).toString('hex')}-pw`;
  secrets.push(password);
  const passwordFile = join(secretsDir, 'admin-password');
  writeFileSync(passwordFile, password + '\n', { mode: 0o600 });

  const port = await freePort();
  const baseURL = `https://localhost:${port}`;
  const log = openSync(localHostLog, 'w');
  const host = spawn('dotnet', ['run', '--project', 'src/Xrmd.Host', '--no-build', '--no-launch-profile'], {
    cwd: repoDir,
    detached: true,
    stdio: ['ignore', log, log],
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: 'Development',
      XRMD_SDK_DIR: sdk,
      ASPNETCORE_URLS: baseURL,
      Kestrel__Certificates__Default__Path: cert,
      Kestrel__Certificates__Default__KeyPath: key,
      ConnectionStrings__Org: `Server=127.0.0.1,${sqlPort};Database=xrmverse_e2e_local;User Id=sa;Password=${saPassword};TrustServerCertificate=True`,
      Auth__Mode: 'Local',
      Auth__Audience: `${baseURL}/`,
      Seed__OrgName: 'e2e-local',
      Seed__AdminObjectId: '00000000-0000-0000-0000-0000000e2e02',
      Seed__AdminName: userName,
      Idp__InitialAdminPasswordFile: passwordFile,
      Ui__RootPath: join(uiDir, 'dist'),
      Files__FileSystem__Root: join(workDir, 'files-local'),
    },
  });
  host.unref();
  state.localHostPid = host.pid;
  writeState(state);
  await waitForHealth(baseURL, host.pid!, localHostLog);

  process.env.XRMD_E2E_LOCAL_BASE_URL = baseURL;
  process.env.XRMD_E2E_LOCAL_USER = userName;
  process.env.XRMD_E2E_LOCAL_PASSWORD = password;
}

export default async function globalSetup(): Promise<void> {
  stopRun(readState()); // leftovers of a run that crashed before its teardown

  const workDir = mkdtempSync(join(tmpdir(), 'xrmverse-e2e-'));
  const container = `xrmverse-e2e-${uniqueSuffix()}`;
  const saPassword = `E2e!${randomBytes(12).toString('hex')}aA1`;
  const signingKey = randomBytes(32).toString('hex');
  secrets.push(saPassword, signingKey);
  const state: RunState = { baseURL: '', signingKey, container, solutionZip: '', workDir };
  writeState(state);

  try {
    const sdk = sdkDir(); // before anything starts: no SDK, no host
    // SQL Server first: it boots while the UI, the host and the fixture build.
    // `-e NAME` without a value: docker takes the value from its own environment, so the password is in no argv.
    run('docker', ['run', '-d', '--name', container, '-e', 'ACCEPT_EULA=Y', '-e', 'MSSQL_SA_PASSWORD', '-p', '127.0.0.1::1433', SQL_IMAGE],
      repoDir, { MSSQL_SA_PASSWORD: saPassword });
    const sqlPort = run('docker', ['port', container, '1433/tcp']).trim().split('\n')[0].split(':').pop();

    run('pnpm', ['run', 'build'], uiDir);
    if (!existsSync(join(uiDir, 'dist', 'index.html'))) throw new Error('The UI build wrote no dist/index.html.');
    run('dotnet', ['build', 'src/Xrmd.Host', '-nologo', '-v', 'q']);
    state.solutionZip = buildSolutionZip(workDir);

    const port = await freePort();
    state.baseURL = `http://127.0.0.1:${port}`;
    const log = openSync(hostLog, 'w');
    const host = spawn('dotnet', ['run', '--project', 'src/Xrmd.Host', '--no-build', '--no-launch-profile'], {
      cwd: repoDir,
      detached: true, // its own process group, so the teardown stops dotnet run and the app it starts
      stdio: ['ignore', log, log],
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: 'Development',
        XRMD_SDK_DIR: sdk,
        ASPNETCORE_URLS: state.baseURL,
        ConnectionStrings__Org: `Server=127.0.0.1,${sqlPort};Database=xrmverse_e2e;User Id=sa;Password=${saPassword};TrustServerCertificate=True`,
        Auth__Mode: 'Development',
        Auth__Audience: `${state.baseURL}/`,
        Auth__DevSigningKey: signingKey,
        Seed__OrgName: 'e2e',
        Seed__AdminObjectId: ADMIN_OBJECT_ID,
        Seed__AdminName: 'Administrator',
        Ui__RootPath: join(uiDir, 'dist'),
        // The file store of file and image columns: the run's own folder, removed by the teardown.
        Files__FileSystem__Root: join(workDir, 'files'),
        // e2e/email.spec.ts sends through GreenMail, which offers no STARTTLS.
        Mail__AllowInsecureSmtp: 'true',
      },
    });
    host.unref();
    state.hostPid = host.pid;
    writeState(state);
    await waitForHealth(state.baseURL, host.pid!);

    await startLocalHost(state, workDir, sdk, sqlPort!, saPassword);

    // For a spec that needs an organization of its own (another database on the same SQL Server, its own host): see
    // startExtraOrganization in harness.ts.
    process.env.XRMD_E2E_SQL_SERVER = `Server=127.0.0.1,${sqlPort};User Id=sa;Password=${saPassword};TrustServerCertificate=True`;
    process.env.XRMD_E2E_SDK_DIR = sdk;
    process.env.XRMD_E2E_BASE_URL = state.baseURL;
    process.env.XRMD_E2E_SIGNING_KEY = signingKey;
    process.env.XRMD_E2E_SOLUTION_ZIP = state.solutionZip;
  } catch (e) {
    stopRun(state);
    throw e instanceof Error ? Object.assign(new Error(redact(e.message)), { stack: redact(e.stack ?? '') }) : e;
  }
}

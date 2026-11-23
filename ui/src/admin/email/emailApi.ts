import type { XrmApi } from '../../shell/api/client';

/** The two kinds of profile xrmverse sends with (plan 4.1b): IMAP/SMTP with Basic, Exchange Online through Microsoft Graph. */
export type ProfileKind = 'smtp' | 'graph';

/** An email server profile as stored. A secret is never read: only whether it is set (`is*set`). */
export interface Profile {
  id: string;
  name: string;
  /** `servertype`: 4 is SMTP, 0 is Graph; anything else (stored by another client) shows as its number. */
  serverType: number | null;
  server: string | null;
  port: number | null;
  ssl: boolean;
  userName: string | null;
  /** `outgoingcredentialretrieval`: 1 in the profile, 0 each mailbox's, 7 the Microsoft Entra application. */
  credentials: number | null;
  tenant: string | null;
  clientId: string | null;
  passwordSet: boolean;
  clientSecretSet: boolean;
  active: boolean;
}

/** A mailbox as stored; the address, approval and statuses are the server's. */
export interface Mailbox {
  id: string;
  name: string | null;
  address: string | null;
  profileId: string | null;
  /** `emailrouteraccessapproval`: 0 Empty, 1 Approved, 2 Pending Approval, 3 Rejected. */
  approval: number;
  /** `outgoingemailstatus`: 0 Not Run, 1 Success, 2 Failure. */
  status: number;
  /** `incomingemaildeliverymethod`: 2 is Server-Side Synchronization, 0 None (plan 4.1c). */
  incomingMethod: number | null;
  incomingEnabled: boolean;
  /** `incomingemailstatus`: 0 Not Run, 1 Success, 2 Failure. */
  incomingStatus: number;
  /** `lastsuccessfulsynccompletedon`: when the mailbox's incoming synchronization last finished. */
  lastSyncOn: string | null;
  testedOn: string | null;
  testScheduled: boolean;
  lastError: string | null;
  active: boolean;
}

/** The mailbox receives through server-side synchronization (plan 4.1c): both the method and the flag say so. */
export const receivesEmail = (m: Pick<Mailbox, 'incomingMethod' | 'incomingEnabled'>) => m.incomingMethod === 2 && m.incomingEnabled;

export const kindOf = (p: Pick<Profile, 'serverType'>): ProfileKind | undefined =>
  p.serverType === 4 ? 'smtp' : p.serverType === 0 ? 'graph' : undefined;

/** The profile's secret is in the profile (SMTP with the credentials in the profile, or Graph's client secret). */
export const holdsSecret = (p: Pick<Profile, 'serverType' | 'credentials'>) => p.serverType === 0 || p.credentials === 1;

/** The profile's secret is set. */
export const secretSet = (p: Profile) => (p.serverType === 0 ? p.clientSecretSet : p.passwordSet);

interface RawProfile {
  emailserverprofileid: string;
  name?: string | null;
  servertype?: number | null;
  outgoingserverlocation?: string | null;
  outgoingportnumber?: number | null;
  outgoingusessl?: boolean | null;
  outgoingusername?: string | null;
  outgoingcredentialretrieval?: number | null;
  exchangeonlinetenantid?: string | null;
  oauthclientid?: string | null;
  isoutgoingpasswordset?: boolean | null;
  isoauthclientsecretset?: boolean | null;
  statecode?: number | null;
}

interface RawMailbox {
  mailboxid: string;
  name?: string | null;
  emailaddress?: string | null;
  _emailserverprofile_value?: string | null;
  emailrouteraccessapproval?: number | null;
  outgoingemailstatus?: number | null;
  incomingemaildeliverymethod?: number | null;
  enabledforincomingemail?: boolean | null;
  incomingemailstatus?: number | null;
  lastsuccessfulsynccompletedon?: string | null;
  testmailboxaccesscompletedon?: string | null;
  testemailconfigurationscheduled?: boolean | null;
  lastsyncerror?: string | null;
  statecode?: number | null;
}

async function all<T>(api: XrmApi, path: string): Promise<T[]> {
  const rows: T[] = [];
  for await (const page of api.list<T>(path)) rows.push(...page);
  return rows;
}

export async function loadProfiles(api: XrmApi): Promise<Profile[]> {
  const select =
    'emailserverprofileid,name,servertype,outgoingserverlocation,outgoingportnumber,outgoingusessl,outgoingusername,outgoingcredentialretrieval,' +
    'exchangeonlinetenantid,oauthclientid,isoutgoingpasswordset,isoauthclientsecretset,statecode';
  return (await all<RawProfile>(api, `emailserverprofiles?$select=${select}&$orderby=name`)).map((r) => ({
    id: r.emailserverprofileid,
    name: r.name ?? '',
    serverType: r.servertype ?? null,
    server: r.outgoingserverlocation ?? null,
    port: r.outgoingportnumber ?? null,
    ssl: r.outgoingusessl === true,
    userName: r.outgoingusername ?? null,
    credentials: r.outgoingcredentialretrieval ?? null,
    tenant: r.exchangeonlinetenantid ?? null,
    clientId: r.oauthclientid ?? null,
    passwordSet: r.isoutgoingpasswordset === true,
    clientSecretSet: r.isoauthclientsecretset === true,
    active: (r.statecode ?? 0) === 0,
  }));
}

export async function loadMailboxes(api: XrmApi): Promise<Mailbox[]> {
  const select =
    'mailboxid,name,emailaddress,_emailserverprofile_value,emailrouteraccessapproval,outgoingemailstatus,testmailboxaccesscompletedon,' +
    'incomingemaildeliverymethod,enabledforincomingemail,incomingemailstatus,lastsuccessfulsynccompletedon,' +
    'testemailconfigurationscheduled,lastsyncerror,statecode';
  return (await all<RawMailbox>(api, `mailboxes?$select=${select}&$orderby=name`)).map((r) => ({
    id: r.mailboxid,
    name: r.name ?? null,
    address: r.emailaddress ?? null,
    profileId: r._emailserverprofile_value ?? null,
    approval: r.emailrouteraccessapproval ?? 0,
    status: r.outgoingemailstatus ?? 0,
    incomingMethod: r.incomingemaildeliverymethod ?? null,
    incomingEnabled: r.enabledforincomingemail === true,
    incomingStatus: r.incomingemailstatus ?? 0,
    lastSyncOn: r.lastsuccessfulsynccompletedon ?? null,
    testedOn: r.testmailboxaccesscompletedon ?? null,
    testScheduled: r.testemailconfigurationscheduled === true,
    lastError: r.lastsyncerror ?? null,
    active: (r.statecode ?? 0) === 0,
  }));
}

/** What the profile form sends. A secret is sent only when one was typed (an empty field keeps the stored one). */
export interface ProfileValues {
  name: string;
  kind: ProfileKind;
  server: string;
  port: string;
  ssl: boolean;
  userName: string;
  /** SMTP only: the user name and password are in the profile (true) or each mailbox's (false). */
  inProfile: boolean;
  tenant: string;
  clientId: string;
  secret: string;
}

/** The row the form writes; the server validates the profile as a whole and its refusal is shown as it is. */
export function profileBody(v: ProfileValues, isNew: boolean): Record<string, unknown> {
  const body: Record<string, unknown> = { name: v.name.trim() };
  if (v.kind === 'graph') {
    Object.assign(body, {
      servertype: 0,
      outgoingauthenticationprotocol: 4,
      outgoingcredentialretrieval: 7,
      exchangeonlinetenantid: v.tenant.trim() || null,
      oauthclientid: v.clientId.trim() || null,
    });
    if (isNew) body.serverauthority = 0;
    if (v.secret) body.oauthclientsecret = v.secret;
  } else {
    Object.assign(body, {
      servertype: 4,
      outgoingauthenticationprotocol: 3,
      outgoingcredentialretrieval: v.inProfile ? 1 : 0,
      outgoingserverlocation: v.server.trim() || null,
      outgoingportnumber: v.port.trim() === '' ? null : Number(v.port.trim()),
      outgoingusessl: v.ssl,
      outgoingusername: v.inProfile ? v.userName.trim() || null : null,
    });
    if (v.secret && v.inProfile) body.outgoingpassword = v.secret;
  }
  return body;
}

export const createProfile = async (api: XrmApi, v: ProfileValues): Promise<void> => {
  await api.post('emailserverprofiles', profileBody(v, true));
};

export const updateProfile = (api: XrmApi, id: string, v: ProfileValues): Promise<void> => api.patch(`emailserverprofiles(${id})`, profileBody(v, false));

/**
 * Approves the email address of a mailbox. The approval is its user's or queue's (`emailrouteraccessapproval`; the mailbox shows a
 * copy the server keeps), so the owner whose default mailbox it is gets the write.
 */
export async function approveMailbox(api: XrmApi, mailboxId: string): Promise<void> {
  for (const [set, key] of [
    ['systemusers', 'systemuserid'],
    ['queues', 'queueid'],
  ] as const) {
    const owners = (await api.page<Record<string, string>>(`${set}?$select=${key}&$filter=_defaultmailbox_value eq ${mailboxId}`)).value;
    if (owners[0]?.[key]) {
      await api.patch(`${set}(${owners[0][key]})`, { emailrouteraccessapproval: 1 });
      return;
    }
  }
  throw new Error(`No user or queue has the mailbox ${mailboxId} as its mailbox.`);
}

/**
 * Links a mailbox to a profile (plan 4.1b task 8, plan 4.1c task 10): the profile, Server-Side Synchronization for outgoing email (2)
 * and outgoing email on. The incoming columns are written only when `incoming` is given (the Receive email switch was touched):
 * true writes 2 and on, false writes None and off; undefined leaves them as they are. The profile is a System Administrator's
 * column; the server judges the write.
 */
export const linkMailbox = (api: XrmApi, mailboxId: string, profileId: string, incoming?: boolean): Promise<void> =>
  api.patch(`mailboxes(${mailboxId})`, {
    'emailserverprofile@odata.bind': `/emailserverprofiles(${profileId})`,
    outgoingemaildeliverymethod: 2,
    enabledforoutgoingemail: true,
    ...(incoming === undefined ? {} : { incomingemaildeliverymethod: incoming ? 2 : 0, enabledforincomingemail: incoming }),
  });

/** Schedules the outgoing test of a mailbox; the server's job writes the outcome on the mailbox. */
export const testMailbox = (api: XrmApi, mailboxId: string): Promise<void> => api.patch(`mailboxes(${mailboxId})`, { testemailconfigurationscheduled: true });

/** The sender policy (xrmverse extension, `xrmverse_EmailSendFrom`): `userOrQueue` (the default, as Dataverse) or `queueOnly`. */
export const EMAIL_SEND_FROM = 'xrmverse_EmailSendFrom';

export interface SenderPolicy {
  definitionId: string;
  defaultValue: string;
  /** The environment's `organizationsetting` row, when there is one. */
  rowId?: string;
  value: string;
}

export async function loadSenderPolicy(api: XrmApi): Promise<SenderPolicy> {
  const definitions = (
    await api.page<{ settingdefinitionid: string; defaultvalue?: string | null }>(
      `settingdefinitions?$select=settingdefinitionid,uniquename,defaultvalue&$filter=uniquename eq '${EMAIL_SEND_FROM}'`,
    )
  ).value;
  const definition = definitions[0];
  if (!definition) throw new Error(`The setting ${EMAIL_SEND_FROM} is not defined.`);
  const rows = (
    await api.page<{ organizationsettingid: string; value?: string | null }>(
      `organizationsettings?$select=organizationsettingid,value&$filter=_settingdefinitionid_value eq ${definition.settingdefinitionid}`,
    )
  ).value;
  const defaultValue = definition.defaultvalue || 'userOrQueue';
  return { definitionId: definition.settingdefinitionid, defaultValue, rowId: rows[0]?.organizationsettingid, value: rows[0]?.value || defaultValue };
}

/** Writes the environment's value: the row is updated when there is one, else created (none is needed for the default). */
export async function saveSenderPolicy(api: XrmApi, policy: SenderPolicy, value: string): Promise<void> {
  if (policy.rowId) {
    await api.patch(`organizationsettings(${policy.rowId})`, { value });
    return;
  }
  if (value === policy.defaultValue) return;
  await api.post('organizationsettings', {
    displayname: EMAIL_SEND_FROM,
    uniquename: EMAIL_SEND_FROM,
    value,
    'settingdefinitionid@odata.bind': `/settingdefinitions(${policy.definitionId})`,
  });
}

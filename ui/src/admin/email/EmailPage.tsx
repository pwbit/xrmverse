import {
  Badge,
  Button,
  MessageBar,
  MessageBarBody,
  Spinner,
  Switch,
  Table,
  TableBody,
  TableCell,
  TableHeader,
  TableHeaderCell,
  TableRow,
  Title3,
} from '@fluentui/react-components';
import { useCallback, useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t } from '../../shell/i18n/t';
import type { en } from '../../shell/i18n/en';
import { useNotify } from '../../shell/notify/Notifications';
import { PageHeader } from '../../shell/layout/PageHeader';
import { ActivateIcon, AddExistingIcon, EditIcon, NewIcon } from '../../shell/icons';
import { RowCommand, SelectCell, SelectHeaderCell, selectableRow, useRowSelection } from '../common/rowSelection';
import {
  approveMailbox,
  linkMailbox,
  createProfile,
  holdsSecret,
  kindOf,
  loadMailboxes,
  loadProfiles,
  loadSenderPolicy,
  receivesEmail,
  saveSenderPolicy,
  secretSet,
  testMailbox,
  updateProfile,
  type Mailbox,
  type Profile,
  type ProfileValues,
  type SenderPolicy,
} from './emailApi';
import { LinkMailboxDialog } from './LinkMailboxDialog';
import { ProfileDialog } from './ProfileDialog';

const messageOf = (e: unknown) => (e instanceof Error ? e.message : String(e));
const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : '');
const label = (key: string) => t(key as keyof typeof en);

/**
 * Email (plan 4.1b and 4.1c, System Administrator only): the email server profiles (secrets write-only: "set" or "not set"), the
 * mailboxes of the users and queues with their approval, both sides of their last test and their last incoming synchronization, and the
 * sender policy (`xrmverse_EmailSendFrom`, an xrmverse extension) as a switch. The command bar acts on the selected profile (Edit) or
 * mailbox (Approve, Link to profile, Test); the server judges every write and its refusal is shown as it is. A test runs as a system
 * job: the page shows "Scheduled" until a reload finds its result.
 */
export function EmailPage() {
  const { api } = useAuth();
  const notify = useNotify();
  const [profiles, setProfiles] = useState<Profile[]>();
  const [mailboxes, setMailboxes] = useState<Mailbox[]>();
  const [policy, setPolicy] = useState<SenderPolicy>();
  const [loadError, setLoadError] = useState<string>();
  const [error, setError] = useState<string>();
  const [busy, setBusy] = useState(false);
  const [editing, setEditing] = useState<Profile | 'new'>();
  const [linking, setLinking] = useState<Mailbox>();
  const profileSelection = useRowSelection(profiles ?? [], (p) => p.id);
  const mailboxSelection = useRowSelection(mailboxes ?? [], (m) => m.id);
  const profile = profileSelection.selected;
  const mailbox = mailboxSelection.selected;

  const reload = useCallback(
    async (live: () => boolean = () => true) => {
      if (!api) return;
      try {
        const [p, m, s] = await Promise.all([loadProfiles(api), loadMailboxes(api), loadSenderPolicy(api)]);
        if (!live()) return;
        setLoadError(undefined);
        setProfiles(p);
        setMailboxes(m);
        setPolicy(s);
      } catch (e) {
        if (live()) setLoadError(messageOf(e));
      }
    },
    [api],
  );

  useEffect(() => {
    let live = true;
    void reload(() => live);
    return () => {
      live = false;
    };
  }, [reload]);

  /** Runs one write, then reads everything again. Resolves whether the write succeeded. */
  async function write(action: () => Promise<void>, done: string): Promise<boolean> {
    setBusy(true);
    setError(undefined);
    try {
      await action();
      notify.success(done);
      await reload();
      return true;
    } catch (e) {
      setError(messageOf(e));
      return false;
    } finally {
      setBusy(false);
    }
  }

  function closeDialog() {
    setEditing(undefined);
    setLinking(undefined);
    setError(undefined);
  }

  function saveLink(profileId: string, incoming?: boolean) {
    const target = linking;
    if (!api || !target) return;
    const profileLabel = profiles?.find((p) => p.id === profileId)?.name ?? profileId;
    const done = t(incoming === true ? 'email.mailboxes.linkedBoth' : 'email.mailboxes.linked', { name: mailboxName(target), profile: profileLabel });
    void write(() => linkMailbox(api, target.id, profileId, incoming), done).then((ok) => ok && closeDialog());
  }

  function saveProfile(values: ProfileValues) {
    const target = editing;
    if (!api || !target) return;
    const action = target === 'new' ? () => createProfile(api, values) : () => updateProfile(api, target.id, values);
    void write(action, t('email.profiles.saved', { name: values.name.trim() })).then((ok) => ok && closeDialog());
  }

  const profileName = (id: string | null) => (id ? profiles?.find((p) => p.id === id)?.name ?? id : '');
  const mailboxName = (m: Mailbox) => m.address ?? m.name ?? m.id;
  const loaded = !!(profiles && mailboxes && policy);
  const queueOnly = policy?.value === 'queueOnly';

  return (
    <section>
      <PageHeader
        title={t('nav.email')}
        actions={
          <>
            <Button appearance="primary" icon={<NewIcon />} disabled={!loaded || busy} onClick={() => setEditing('new')}>
              {t('email.profiles.new')}
            </Button>
            {profile && (
              <RowCommand icon={<EditIcon />} disabled={busy} onClick={() => setEditing(profile)}>
                {t('email.profiles.edit')}
              </RowCommand>
            )}
            {mailbox && (
              <>
                <RowCommand
                  icon={<ActivateIcon />}
                  disabled={busy || mailbox.approval === 1}
                  onClick={() => void write(() => approveMailbox(api!, mailbox.id), t('email.mailboxes.approved', { name: mailboxName(mailbox) }))}
                >
                  {t('email.mailboxes.approve')}
                </RowCommand>
                <RowCommand icon={<AddExistingIcon />} disabled={busy || !profiles?.length} onClick={() => setLinking(mailbox)}>
                  {t('email.mailboxes.link')}
                </RowCommand>
                <RowCommand
                  disabled={busy || mailbox.testScheduled}
                  onClick={() => void write(() => testMailbox(api!, mailbox.id), t('email.mailboxes.testScheduled', { name: mailboxName(mailbox) }))}
                >
                  {t('email.mailboxes.test')}
                </RowCommand>
              </>
            )}
          </>
        }
      />
      <p>{t('email.intro')}</p>
      {loadError && (
        <MessageBar intent="error">
          <MessageBarBody>{t('email.loadFailed', { detail: loadError })}</MessageBarBody>
        </MessageBar>
      )}
      {!loaded && !loadError && <Spinner label={t('common.loading')} />}
      {error && !editing && !linking && (
        <MessageBar intent="error">
          <MessageBarBody>{error}</MessageBarBody>
        </MessageBar>
      )}
      {policy && (
        <>
          <Title3 as="h3" block>{t('email.policy.title')}</Title3>
          <Switch
            label={t('email.policy.queueOnly')}
            checked={queueOnly}
            disabled={busy}
            onChange={(_, d) =>
              void write(() => saveSenderPolicy(api!, policy, d.checked ? 'queueOnly' : 'userOrQueue'), t('email.policy.saved'))
            }
          />
          <p>{t('email.policy.hint')}</p>
        </>
      )}
      {profiles && (
        <>
          <Title3 as="h3" block>{t('email.profiles.title')}</Title3>
          {profiles.length === 0 ? (
            <p>{t('email.profiles.empty')}</p>
          ) : (
            <Table aria-label={t('email.profiles.title')}>
              <TableHeader>
                <TableRow>
                  <SelectHeaderCell />
                  <TableHeaderCell>{t('email.col.name')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.type')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.server')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.credentials')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.secret')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.state')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {profiles.map((p) => {
                  const kind = kindOf(p);
                  return (
                    <TableRow key={p.id} {...selectableRow(profileSelection, p)}>
                      <SelectCell selection={profileSelection} item={p} name={p.name} />
                      <TableCell>{p.name}</TableCell>
                      <TableCell>{kind ? t(`email.kind.${kind}`) : String(p.serverType ?? '')}</TableCell>
                      <TableCell>{(kind === 'graph' ? p.tenant : p.server) ?? ''}</TableCell>
                      <TableCell>
                        {kind === 'graph'
                          ? t('email.credentials.application')
                          : p.credentials === 0
                            ? t('email.credentials.mailbox')
                            : t('email.credentials.profile')}
                      </TableCell>
                      <TableCell>
                        {!holdsSecret(p) ? t('email.secret.none') : secretSet(p) ? t('email.secret.set') : t('email.secret.notSet')}
                      </TableCell>
                      <TableCell>{p.active ? t('email.state.active') : t('email.state.inactive')}</TableCell>
                    </TableRow>
                  );
                })}
              </TableBody>
            </Table>
          )}
        </>
      )}
      {mailboxes && (
        <>
          <Title3 as="h3" block>{t('email.mailboxes.title')}</Title3>
          {mailboxes.length === 0 ? (
            <p>{t('email.mailboxes.empty')}</p>
          ) : (
            <Table aria-label={t('email.mailboxes.title')}>
              <TableHeader>
                <TableRow>
                  <SelectHeaderCell />
                  <TableHeaderCell>{t('email.col.name')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.address')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.approval')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.profile')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.status')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.incoming')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.lastSync')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.testedOn')}</TableHeaderCell>
                  <TableHeaderCell>{t('email.col.lastError')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {mailboxes.map((m) => (
                  <TableRow key={m.id} {...selectableRow(mailboxSelection, m)}>
                    <SelectCell selection={mailboxSelection} item={m} name={mailboxName(m)} />
                    <TableCell>{m.name ?? ''}</TableCell>
                    <TableCell>{m.address ?? ''}</TableCell>
                    <TableCell>{label(`email.approval.${m.approval}`)}</TableCell>
                    <TableCell>{profileName(m.profileId)}</TableCell>
                    <TableCell>
                      {m.testScheduled ? (
                        t('email.status.scheduled')
                      ) : (
                        <Badge appearance="tint" color={m.status === 1 ? 'success' : m.status === 2 ? 'danger' : 'informative'}>
                          {label(`email.status.${m.status}`)}
                        </Badge>
                      )}
                    </TableCell>
                    <TableCell>
                      {!receivesEmail(m) ? (
                        t('email.incoming.off')
                      ) : (
                        <Badge appearance="tint" color={m.incomingStatus === 1 ? 'success' : m.incomingStatus === 2 ? 'danger' : 'informative'}>
                          {label(`email.status.${m.incomingStatus}`)}
                        </Badge>
                      )}
                    </TableCell>
                    <TableCell>{when(m.lastSyncOn)}</TableCell>
                    <TableCell>{when(m.testedOn)}</TableCell>
                    <TableCell>{m.status === 2 ? m.lastError ?? '' : ''}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </>
      )}
      {linking && profiles && (
        <LinkMailboxDialog
          mailbox={linking}
          mailboxName={mailboxName(linking)}
          profiles={profiles}
          busy={busy}
          error={error}
          onClose={closeDialog}
          onSave={saveLink}
        />
      )}
      {editing && (
        <ProfileDialog
          profile={editing === 'new' ? undefined : editing}
          busy={busy}
          error={error}
          onClose={closeDialog}
          onSave={saveProfile}
        />
      )}
    </section>
  );
}

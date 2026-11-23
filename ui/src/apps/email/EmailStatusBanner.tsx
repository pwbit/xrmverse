import { MessageBar, MessageBarBody, MessageBarTitle } from '@fluentui/react-components';
import { useContext, useEffect, useState } from 'react';
import { useAuth } from '../../shell/auth/AuthProvider';
import { t, uiLocale } from '../../shell/i18n/t';
import { DisplayZoneContext } from '../displayZone';
import { EMAIL_COMPLETED, EMAIL_OPEN, EMAIL_STATUS } from './emailCommands';

/** `asyncoperation.operationtype` of the email send job (xrmverse's own, `AsyncOperationQueue.EmailSend`). */
export const EMAIL_SEND_OPERATION = 99011;

/** How often a Pending Send email is read again until its send ends. */
const POLL_MS = 3000;

interface EmailRow {
  statecode?: number | null;
  statuscode?: number | null;
  senton?: string | null;
  messageid?: string | null;
  sender?: string | null;
}

export interface EmailStatusBannerProps {
  /** The email's id. */
  id: string;
  /** The email as the form last read it (`statecode` and `statuscode`). */
  record: Record<string, unknown> | undefined;
  /** The send ended (Sent or Failed) while the banner showed Pending Send: the form reads the email again. */
  onChanged: () => void;
  pollMs?: number;
}

const kindOf = (state: unknown, status: unknown): 'sending' | 'sent' | 'failed' | undefined =>
  state === EMAIL_COMPLETED && status === EMAIL_STATUS.pendingSend
    ? 'sending'
    : state === EMAIL_COMPLETED && status === EMAIL_STATUS.sent
      ? 'sent'
      : state === EMAIL_OPEN && status === EMAIL_STATUS.failed
        ? 'failed'
        : undefined;

/**
 * The send status of an email (plan 4.1b, task 9), under the form's title: Pending Send ("sending", the form is read-only until
 * the send ends; the email is read again every few seconds and the form reloads when it changes), Sent (when, from which
 * address, the Message-ID) and Failed (the reason, the message of the email's newest send job). A draft shows nothing.
 */
export function EmailStatusBanner({ id, record, onChanged, pollMs = POLL_MS }: EmailStatusBannerProps) {
  const { api } = useAuth();
  const zone = useContext(DisplayZoneContext);
  const kind = kindOf(record?.statecode, record?.statuscode);
  const [row, setRow] = useState<EmailRow>();
  const [reason, setReason] = useState<string>();

  useEffect(() => {
    if (!api || !kind) return;
    let live = true;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const read = async () => {
      try {
        const fresh = await api.get<EmailRow>(`emails(${id})?$select=statecode,statuscode,senton,messageid,sender`);
        if (!live) return;
        setRow(fresh);
        if (kind === 'sending' && kindOf(fresh.statecode, fresh.statuscode) !== 'sending') return onChanged();
      } catch {
        // A failed read keeps the banner as it is; the next poll tries again.
      }
      if (live && kind === 'sending') timer = setTimeout(() => void read(), pollMs);
    };
    void read();
    return () => {
      live = false;
      if (timer) clearTimeout(timer);
    };
    // onChanged is the page's; the banner reads again when the record's status changes.
  }, [api, id, kind, pollMs]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!api || kind !== 'failed') return;
    let live = true;
    // asyncoperation.regardingobjectid is a plain UniqueIdentifier here, not a lookup.
    const filter = `regardingobjectid eq ${id} and operationtype eq ${EMAIL_SEND_OPERATION}`;
    api
      .get<{ value: { message?: string | null; friendlymessage?: string | null }[] }>(
        `asyncoperations?$select=message,friendlymessage&$filter=${encodeURIComponent(filter)}&$orderby=createdon desc&$top=1`,
      )
      .then(
        (r) => live && setReason(r.value[0]?.friendlymessage || r.value[0]?.message || undefined),
        () => live && setReason(undefined),
      );
    return () => {
      live = false;
    };
  }, [api, id, kind]);

  if (!kind) return null;
  if (kind === 'sending')
    return (
      <MessageBar intent="info" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>{t('email.status.sending')}</MessageBarTitle>
          {t('email.status.sendingBody')}
        </MessageBarBody>
      </MessageBar>
    );
  if (kind === 'failed')
    return (
      <MessageBar intent="error" layout="multiline">
        <MessageBarBody>
          <MessageBarTitle>{t('email.status.failed')}</MessageBarTitle>
          {reason ? `${reason} ` : ''}
          {t('email.status.failedBody')}
        </MessageBarBody>
      </MessageBar>
    );
  const sentOn = row?.senton ? new Date(row.senton) : undefined;
  const when =
    sentOn && !Number.isNaN(sentOn.getTime())
      ? new Intl.DateTimeFormat(uiLocale(), { dateStyle: 'short', timeStyle: 'short', ...(zone ? { timeZone: zone } : {}) }).format(sentOn)
      : undefined;
  return (
    <MessageBar intent="success" layout="multiline">
      <MessageBarBody>
        <MessageBarTitle>{t('email.status.sent')}</MessageBarTitle>
        {[when && t('email.status.sentOn', { when }), row?.sender && t('email.status.sentFrom', { address: row.sender }), row?.messageid && t('email.status.messageId', { id: row.messageid })]
          .filter(Boolean)
          .join(' · ')}
      </MessageBarBody>
    </MessageBar>
  );
}

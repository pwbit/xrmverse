import { Field, Text, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { t } from '../../../shell/i18n/t';
import type { AttributeMeta } from '../metadata';

/** A padlock: marks a column secured by field security. */
export function LockIcon() {
  return (
    <svg role="img" aria-label={t('fieldSecurity.lock')} width="12" height="12" viewBox="0 0 16 16" fill="currentColor" style={{ color: tokens.colorNeutralForeground3 }}>
      <title>{t('fieldSecurity.lock')}</title>
      <path d="M8 1a3.5 3.5 0 0 0-3.5 3.5V6H4a1.5 1.5 0 0 0-1.5 1.5v6A1.5 1.5 0 0 0 4 15h8a1.5 1.5 0 0 0 1.5-1.5v-6A1.5 1.5 0 0 0 12 6h-.5V4.5A3.5 3.5 0 0 0 8 1Zm2.5 5h-5V4.5a2.5 2.5 0 0 1 5 0V6Z" />
    </svg>
  );
}

/** The editor of a secured column, with the padlock before it. */
export function SecuredField({ children }: { children: ReactNode }) {
  return (
    <div style={{ display: 'flex', alignItems: 'flex-start', gap: tokens.spacingHorizontalXS }}>
      <span style={{ paddingTop: tokens.spacingVerticalXS }}>
        <LockIcon />
      </span>
      <div style={{ flexGrow: 1 }}>{children}</div>
    </div>
  );
}

/** A secured column the record leaves out because the caller cannot read it: shown, never as an empty value. */
export function NoAccessField({ attr }: { attr: AttributeMeta }) {
  return (
    <Field label={attr.displayName}>
      <Text>{t('fieldSecurity.noAccess')}</Text>
    </Field>
  );
}

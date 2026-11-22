import { Badge, Caption1, makeStyles, Title2, tokens } from '@fluentui/react-components';
import type { ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { t } from '../../shell/i18n/t';
import { PreviousPageIcon } from '../../shell/icons';
import type { ProcessKind, ProcessState } from '../api/studioApi';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexWrap: 'wrap',
    alignItems: 'center',
    columnGap: tokens.spacingHorizontalM,
    rowGap: tokens.spacingVerticalS,
    marginBottom: tokens.spacingVerticalM,
  },
  back: { display: 'inline-flex', alignItems: 'center', columnGap: tokens.spacingHorizontalXS },
  title: { margin: 0, minWidth: 0, overflowWrap: 'anywhere' },
  facts: { color: tokens.colorNeutralForeground3 },
  badges: { display: 'flex', flexWrap: 'wrap', columnGap: tokens.spacingHorizontalXS },
  commands: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: tokens.spacingHorizontalS, marginLeft: 'auto' },
});

export interface EditorHeaderProps {
  name: string;
  kind?: ProcessKind;
  /** The primary table; `none` (a global action) shows as "(global)". */
  entity?: string;
  mode?: 'realtime' | 'background' | null;
  /** The saved state; undefined for a process not saved yet (or not known yet). */
  state?: ProcessState;
  managed?: boolean;
  unsaved?: boolean;
  /** Shown before the commands (the Canvas | Code toggle). */
  middle?: ReactNode;
  commands?: ReactNode;
}

/** The editor's header (spec §4.3): ← Workflows · name · kind/table/mode · state and managed badges · toggle · commands. */
export function EditorHeader(p: EditorHeaderProps) {
  const styles = useStyles();
  const facts = [
    p.kind && t(`studio.kind.${p.kind}`),
    p.entity && (p.entity === 'none' ? t('studio.list.global') : p.entity),
    p.mode && t(`studio.mode.${p.mode}`),
  ].filter(Boolean);
  return (
    <div role="region" aria-label={t('studio.editor.header')} className={styles.root}>
      <Link to="/studio/workflows" className={styles.back}>
        <PreviousPageIcon aria-hidden />
        {t('studio.editor.back')}
      </Link>
      <Title2 as="h1" className={styles.title}>
        {p.name}
      </Title2>
      {facts.length > 0 && <Caption1 className={styles.facts}>{facts.join(' · ')}</Caption1>}
      <span className={styles.badges}>
        {p.state && (
          <Badge appearance="tint" color={p.state === 'activated' ? 'success' : 'informative'}>
            {t(`studio.state.${p.state}`)}
          </Badge>
        )}
        {p.managed && (
          <Badge appearance="outline" color="informative">
            {t('studio.list.managed')}
          </Badge>
        )}
        {p.unsaved && (
          <Badge appearance="outline" color="warning">
            {t('studio.editor.unsaved')}
          </Badge>
        )}
      </span>
      {p.middle}
      <div className={styles.commands}>{p.commands}</div>
    </div>
  );
}

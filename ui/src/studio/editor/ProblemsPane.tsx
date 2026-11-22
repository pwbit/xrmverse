import { Button, Caption1, makeStyles, Subtitle2, tokens } from '@fluentui/react-components';
import { ErrorCircleRegular, WarningRegular } from '@fluentui/react-icons';
import { t } from '../../shell/i18n/t';
import type { EditorProblem } from './problems';

const useStyles = makeStyles({
  root: {
    borderTop: `${tokens.strokeWidthThin} solid ${tokens.colorNeutralStroke2}`,
    paddingTop: tokens.spacingVerticalS,
    maxHeight: '200px',
    overflowY: 'auto',
  },
  list: { listStyleType: 'none', margin: 0, padding: 0 },
  item: { display: 'flex', alignItems: 'center', columnGap: tokens.spacingHorizontalS, minHeight: tokens.lineHeightBase400 },
  error: { color: tokens.colorPaletteRedForeground1, flexShrink: 0 },
  warning: { color: tokens.colorPaletteMarigoldForeground1, flexShrink: 0 },
  at: { fontFamily: tokens.fontFamilyMonospace, color: tokens.colorNeutralForeground3, marginRight: tokens.spacingHorizontalS },
  go: { justifyContent: 'flex-start', textAlign: 'start', fontWeight: tokens.fontWeightRegular, minWidth: 0 },
  text: { paddingLeft: tokens.spacingHorizontalS },
});

/**
 * The problems of the document (spec §4.3): the reader's, the server's errors and its assumed shapes (warnings). A problem with
 * a line is a button: `onOpen` takes the user there (the cursor in the code view, the card on the canvas).
 */
export function ProblemsPane({ problems, checkError, onOpen }: { problems: EditorProblem[]; checkError?: string; onOpen(problem: EditorProblem): void }) {
  const styles = useStyles();
  const count = problems.length;
  return (
    <section className={styles.root} aria-label={t('studio.editor.problems', { count })}>
      <Subtitle2 as="h2">{t('studio.editor.problems', { count })}</Subtitle2>
      {count === 0 && !checkError && <Caption1 block>{t('studio.editor.problems.none')}</Caption1>}
      <ul className={styles.list}>
        {checkError && (
          <li className={styles.item}>
            <WarningRegular className={styles.warning} aria-label={t('studio.editor.problem.warning')} />
            <span className={styles.text}>{t('studio.editor.problems.checkFailed', { detail: checkError })}</span>
          </li>
        )}
        {problems.map((p, i) => {
          const icon =
            p.severity === 'error' ? (
              <ErrorCircleRegular className={styles.error} aria-label={t('studio.editor.problem.error')} />
            ) : (
              <WarningRegular className={styles.warning} aria-label={t('studio.editor.problem.warning')} />
            );
          return (
            <li key={i} className={styles.item}>
              {icon}
              {p.line !== undefined ? (
                <Button appearance="transparent" size="small" className={styles.go} onClick={() => onOpen(p)}>
                  <span className={styles.at}>
                    {p.line}:{p.column ?? 1}
                  </span>
                  {p.message}
                </Button>
              ) : (
                <span className={styles.text}>{p.message}</span>
              )}
            </li>
          );
        })}
      </ul>
    </section>
  );
}

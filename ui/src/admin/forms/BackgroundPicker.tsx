import { Button, ColorSwatch, makeStyles, SwatchPicker, tokens } from '@fluentui/react-components';
import { useId } from 'react';
import { BACKGROUND_NAMES, backgroundColorOf, type BackgroundColor, type BackgroundName } from '../../parsers/presentationJson';
import { BACKGROUND_TOKENS } from '../../renderers/modern/background';
import { t } from '../../shell/i18n/t';

const useStyles = makeStyles({
  root: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', columnGap: tokens.spacingHorizontalM, rowGap: tokens.spacingVerticalXS },
  label: { minWidth: '8rem' },
  custom: { display: 'inline-flex', alignItems: 'center', columnGap: tokens.spacingHorizontalXS },
  native: { inlineSize: '32px', blockSize: '24px', padding: 0, border: 'none', background: 'none', cursor: 'pointer' },
});

const label = (name: BackgroundName) => t(`forms.look.background.${name}` as const);

export interface BackgroundPickerProps {
  /** What the picker is for (a section or a field); the swatches and the custom colour are labelled with it. */
  label: string;
  value: BackgroundColor | undefined;
  onChange: (value: BackgroundColor | undefined) => void;
}

/**
 * A background colour: one of the named colours (they follow the light or dark theme), a free colour from the browser's
 * colour picker (`#rrggbb`, the same in both themes), or none.
 */
export function BackgroundPicker({ label: what, value, onChange }: BackgroundPickerProps) {
  const styles = useStyles();
  const customId = useId();
  const custom = value?.startsWith('#') ? value : undefined;
  return (
    <div className={styles.root} role="group" aria-label={what}>
      <span className={styles.label}>{what}</span>
      <SwatchPicker
        aria-label={what}
        size="small"
        shape="circular"
        selectedValue={value ?? ''}
        onSelectionChange={(_, d) => onChange(backgroundColorOf(d.selectedValue))}
      >
        {BACKGROUND_NAMES.map((name) => (
          <ColorSwatch key={name} value={name} color={BACKGROUND_TOKENS[name]} aria-label={label(name)} title={label(name)} />
        ))}
        {custom && <ColorSwatch value={custom} color={custom} aria-label={t('forms.look.background.custom')} title={custom} />}
      </SwatchPicker>
      <span className={styles.custom}>
        <input
          id={customId}
          type="color"
          className={styles.native}
          defaultValue={custom}
          aria-label={`${t('forms.look.background.custom')}: ${what}`}
          onChange={(e) => onChange(backgroundColorOf(e.target.value))}
        />
        <label htmlFor={customId}>{t('forms.look.background.custom')}</label>
      </span>
      {value && (
        <Button size="small" appearance="subtle" onClick={() => onChange(undefined)} aria-label={`${t('forms.look.background.clear')}: ${what}`}>
          {t('forms.look.background.clear')}
        </Button>
      )}
    </div>
  );
}

import { SearchBox } from '@fluentui/react-components';
import { useState } from 'react';
import { t } from '../../shell/i18n/t';

/** The quick find box: Enter searches the text, clearing it goes back to the selected view (`onSearch('')`). */
export function QuickFind({ onSearch }: { onSearch: (text: string) => void }) {
  const [value, setValue] = useState('');
  const [applied, setApplied] = useState('');
  const apply = (text: string) => {
    if (text === applied) return;
    setApplied(text);
    onSearch(text);
  };
  return (
    <SearchBox
      aria-label={t('apps.views.search')}
      placeholder={t('apps.views.search')}
      value={value}
      onChange={(_e, data) => {
        setValue(data.value);
        if (data.value.trim() === '') apply('');
      }}
      onKeyDown={(e) => {
        if (e.key === 'Enter') apply(value.trim());
      }}
    />
  );
}

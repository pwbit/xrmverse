import { Text } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';
import { FileColumnControls } from './FileColumnControls';
import type { FieldProps } from './fieldProps';
import { useFileColumn } from './useFileColumn';

/**
 * `file` columns: the file's name (the `<column>_name` companion), upload (one request up to 4 MB, chunked beyond),
 * download under the server's file name, and delete. Only on a record that exists.
 */
export function FileField(p: FieldProps) {
  const column = p.attr.logicalName;
  const nameProperty = `${column}_name`;
  const file = useFileColumn(p.attr, p.context, [column, nameProperty], { uploaded: 'field.file.uploaded', deleted: 'field.file.deleted' });
  const id = file.values[column];
  const name = file.values[nameProperty];
  const shown = typeof name === 'string' && name ? name : typeof id === 'string' ? id : undefined;
  return (
    <FileColumnControls
      {...p}
      column={file}
      filled={id !== null && id !== undefined}
      downloadable
      confirm={{
        title: t('field.file.delete.title'),
        body: t('field.file.delete.body', { name: shown ?? '', label: p.attr.displayName }),
        label: t('field.file.delete.confirm'),
      }}
    >
      <Text>{shown ?? t('field.file.none')}</Text>
    </FileColumnControls>
  );
}

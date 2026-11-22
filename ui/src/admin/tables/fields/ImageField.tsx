import { Button, Text } from '@fluentui/react-components';
import { t } from '../../../shell/i18n/t';
import { FileColumnControls } from './FileColumnControls';
import type { FieldProps } from './fieldProps';
import { useFileColumn } from './useFileColumn';

/** The thumbnail's type: the server keeps a JPEG input as JPEG and makes anything else PNG (base64 of JPEG starts `/9j/`). */
const thumbnailType = (base64: string) => (base64.startsWith('/9j/') ? 'image/jpeg' : 'image/png');

/**
 * `image` columns: the 144×144 thumbnail the record carries (base64), upload, delete, and the download of the full-size
 * image when the column keeps it (`CanStoreFullImage`). Only on a record that exists.
 */
export function ImageField(p: FieldProps) {
  const column = p.attr.logicalName;
  const image = useFileColumn(p.attr, p.context, [column], { uploaded: 'field.image.uploaded', deleted: 'field.image.deleted' });
  const thumbnail = image.values[column];
  const filled = typeof thumbnail === 'string' && thumbnail !== '';
  const label = p.attr.displayName;
  return (
    <FileColumnControls
      {...p}
      column={image}
      filled={filled}
      accept="image/*"
      downloadable={false}
      actions={
        p.attr.canStoreFullImage && (
          <Button
            size="small"
            disabled={p.disabled || image.busy}
            aria-label={t('field.image.downloadFullLabel', { label })}
            onClick={() => void image.download(true)}
          >
            {t('field.image.downloadFull')}
          </Button>
        )
      }
      confirm={{ title: t('field.image.delete.title'), body: t('field.image.delete.body', { label }), label: t('field.image.delete.confirm') }}
    >
      {filled ? (
        <img src={`data:${thumbnailType(thumbnail)};base64,${thumbnail}`} alt={t('field.image.thumbnail', { label })} width={144} height={144} />
      ) : (
        <Text>{t('field.image.none')}</Text>
      )}
    </FileColumnControls>
  );
}

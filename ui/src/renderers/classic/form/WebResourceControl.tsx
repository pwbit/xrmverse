import { makeStyles, MessageBar, MessageBarBody, Spinner, tokens } from '@fluentui/react-components';
import { useContext } from 'react';
import { FrameUrlError, frameParameters, frameSandbox, initialFrameSrc } from '../../../client-api/frameSource';
import { WebResourceNameError } from '../../../client-api/scriptHost';
import { checkedAddress, useWebResourceCheck } from '../../../client-api/useWebResourceCheck';
import { XrmHostContext } from '../../../client-api/xrm';
import { useUiOverlay } from '../../../client-api/uiOverlay';
import type { Cell, WebResourceControl as WebResourceModel } from '../../../model/form';
import { t } from '../../../shell/i18n/t';
import type { ControlEnv } from './controlEnv';

const ROW_HEIGHT = '3rem';

const useStyles = makeStyles({
  frame: { display: 'block', width: '100%', minHeight: '6rem', backgroundColor: tokens.colorNeutralBackground1 },
  border: { border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium },
  noBorder: { border: 'none' },
});

export interface WebResourceControlProps {
  cell: Cell;
  control: WebResourceModel;
  env: ControlEnv;
}

/**
 * A web resource or IFRAME control as an IFRAME. The source is the web resource (with its `data`) or the IFRAME's address,
 * plus the record parameters when the control passes them; a script's `setSrc` replaces it. A web resource is same origin,
 * so its page reaches `parent.Xrm`. An address on another origin is sandboxed (no shell origin, so no `parent.Xrm`), and
 * an address with another scheme than http or https is refused with a message.
 */
export function WebResourceControl({ cell, control, env }: WebResourceControlProps) {
  const styles = useStyles();
  const host = useContext(XrmHostContext);
  useUiOverlay(env.overlay);
  const label = control.iframe ? control.url : control.url.split('?')[0]!;
  let src: string | undefined;
  let sandbox: string | undefined;
  let refusal: string | undefined;
  try {
    src = env.overlay?.element(cell).src ?? initialFrameSrc(control, frameParameters(env, host?.globalContext));
    sandbox = frameSandbox(src, control.restrictScripting);
  } catch (e) {
    if (e instanceof WebResourceNameError) refusal = t('webresource.invalidName', { name: control.url });
    else if (e instanceof FrameUrlError) refusal = e.message === 'empty' ? t('webresource.missingName') : t('webresource.urlRefused', { url: control.url });
    else throw e;
  }
  // A web resource of this server is asked for first; an external address is not.
  const check = useWebResourceCheck(src === undefined ? undefined : checkedAddress(src));
  if (refusal !== undefined || src === undefined) {
    return (
      <MessageBar intent="warning">
        <MessageBarBody>{refusal}</MessageBarBody>
      </MessageBar>
    );
  }
  if (check.status === 'checking') return <Spinner size="tiny" label={t('common.loading')} />;
  if (check.status !== 'ready') {
    return (
      <MessageBar intent={check.status === 'missing' ? 'warning' : 'error'}>
        <MessageBarBody>{check.status === 'missing' ? t('webresource.notFound', { name: label }) : t('apps.error', { detail: check.detail })}</MessageBarBody>
      </MessageBar>
    );
  }
  return (
    <iframe
      // A new address is a new frame: the page loads again, and its sandbox is the one of the new address.
      key={src}
      ref={(el) => env.overlay?.setFrame(cell, el)}
      className={`${styles.frame} ${control.border ? styles.border : styles.noBorder}`}
      style={{ height: `calc(${Math.max(cell.rowSpan, 1)} * ${ROW_HEIGHT})` }}
      src={src}
      title={t('xrm.webResource.frame', { name: label })}
      scrolling={control.scrolling}
      {...(sandbox !== undefined ? { sandbox } : {})}
    />
  );
}

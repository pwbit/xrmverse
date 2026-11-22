import { makeStaticStyles, tokens } from '@fluentui/react-components';
import { brandFontStack, xrmverseTheme } from './theme';

/**
 * Page-wide rules for the web admin and the model-driven apps: no browser margin around the layout, and plain router
 * links (no Fluent class) in the brand link colour, underlined on hover only.
 */
const useStaticStyles = makeStaticStyles({
  body: { margin: 0, fontFamily: brandFontStack, fontWeight: xrmverseTheme.fontWeightRegular },
  'a:not([class])': { color: tokens.colorBrandForegroundLink, textDecorationLine: 'none' },
  'a:not([class]):hover': { textDecorationLine: 'underline' },
});

export function GlobalStyles() {
  useStaticStyles();
  return null;
}

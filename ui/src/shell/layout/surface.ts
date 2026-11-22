import { makeStyles, tokens } from '@fluentui/react-components';

/**
 * The surfaces shared by the web admin and the model-driven shell (the owner's choice, 2026-11-17): a section is a card
 * outlined by Fluent's shadow2 with a line under its title, and a command bar stands out from the page with shadow2.
 */
export const useSurfaceStyles = makeStyles({
  section: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalM,
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    marginBlock: tokens.spacingVerticalS,
    boxShadow: tokens.shadow2,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  // The section's line sits under its title, at the start of the section.
  sectionTitle: { margin: 0, paddingBottom: tokens.spacingVerticalS, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  commandBar: { boxShadow: tokens.shadow2, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1 },
});

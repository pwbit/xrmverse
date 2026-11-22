/** @mentions in a post's text: `@[Entity,ID,"Display String"]`, no spaces after the commas (docs: timeline posts, mentions). */
export type MentionPart = { kind: 'text'; text: string } | { kind: 'mention'; entity: string; id: string; display: string };

/** Entity (no `,` `]` or space), ID (same), then the display string in double quotes (no `"` or `]` inside). */
const MENTION = /@\[([^,\]\s]+),([^,\]\s]+),"([^"\]]*)"\]/g;

/** The text split into text and mention parts; anything that is not a well-formed mention stays text, adjacent text is merged. */
export function parseMentions(text: string): MentionPart[] {
  const parts: MentionPart[] = [];
  const addText = (s: string) => {
    if (!s) return;
    const last = parts[parts.length - 1];
    if (last?.kind === 'text') last.text += s;
    else parts.push({ kind: 'text', text: s });
  };
  let from = 0;
  for (const m of text.matchAll(MENTION)) {
    addText(text.slice(from, m.index));
    parts.push({ kind: 'mention', entity: m[1], id: m[2], display: m[3] });
    from = m.index + m[0].length;
  }
  addText(text.slice(from));
  return parts;
}

/** `@[8,{guid},"Display"]` for a user: the guid braced and lower case (given with or without braces, any case); `"` and `]` leave the display. */
export function formatMention(userId: string, display: string): string {
  const guid = userId.trim().replace(/^\{|\}$/g, '').toLowerCase();
  return `@[8,{${guid}},"${display.replace(/["\]]/g, '')}"]`;
}

/** A mention of a user: Entity `8` or `systemuser`. */
export function isUserMention(p: MentionPart): boolean {
  return p.kind === 'mention' && (p.entity === '8' || p.entity.toLowerCase() === 'systemuser');
}

/**
 * Accessible name of an element as a screen reader gets it, reduced to what the property editors
 * use: `aria-labelledby` (ids joined by space), else `aria-label`, else a `<label for>` or
 * wrapping `<label>`. Test helper only.
 */
export function accessibleName(element: Element): string {
  // Content hidden from assistive technology (e.g. the "*" of required properties) is not read.
  const visibleText = (node: Element | null) => {
    if (!node) {
      return '';
    }
    const copy = node.cloneNode(true) as Element;
    copy.querySelectorAll('[aria-hidden="true"]').forEach((hidden) => hidden.remove());
    return copy.textContent;
  };
  const clean = (text: string | null | undefined) => (text ?? '').replace(/\s+/g, ' ').trim();
  const labelledBy = element.getAttribute('aria-labelledby');
  if (labelledBy) {
    return labelledBy
      .split(/\s+/)
      .map((id) => clean(visibleText(element.ownerDocument.getElementById(id))))
      .join(' ')
      .trim();
  }
  const label = element.getAttribute('aria-label');
  if (label) {
    return clean(label);
  }
  const id = element.getAttribute('id');
  const forLabel = id ? element.ownerDocument.querySelector(`label[for="${id}"]`) : null;
  return clean(visibleText(forLabel ?? element.closest('label')));
}

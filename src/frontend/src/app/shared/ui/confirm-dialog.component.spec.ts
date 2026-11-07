import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ConfirmDialogComponent } from './confirm-dialog.component';

@Component({
  imports: [ConfirmDialogComponent],
  template: `
    <button type="button" data-testid="opener" (click)="open.set(true)">Löschen</button>
    <ag-confirm-dialog
      [open]="open()"
      title="Wirklich löschen?"
      message="Das lässt sich nicht rückgängig machen."
      confirmLabel="Löschen"
      [busy]="busy()"
      (confirmed)="confirmed = confirmed + 1"
      (cancelled)="cancelled = cancelled + 1; open.set(false)"
    />
  `,
})
class HostComponent {
  readonly open = signal(false);
  readonly busy = signal(false);
  confirmed = 0;
  cancelled = 0;
}

describe('ConfirmDialogComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;

  // The dialog renders into the CDK overlay container in <body>, not into the fixture.
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    document.body.querySelector<T>(`[data-testid="${id}"]`);

  async function stable(): Promise<void> {
    await fixture.whenStable();
    await new Promise((resolve) => setTimeout(resolve));
    await fixture.whenStable();
  }

  async function open(): Promise<void> {
    host.open.set(true);
    await stable();
  }

  beforeEach(async () => {
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
    await stable();
  });

  it('render_Closed_ShowsNothing', () => {
    expect(q('confirm-dialog')).toBeNull();
  });

  it('render_Open_IsAlertDialogWithTitleMessageAndLabels', async () => {
    await open();

    const dialog = q('confirm-dialog')!;
    const container = dialog.closest('[role="alertdialog"]')!;
    expect(container).not.toBeNull();
    expect(container.getAttribute('aria-modal')).toBe('true');
    const title = document.getElementById(container.getAttribute('aria-labelledby')!);
    expect(title?.textContent?.trim()).toBe('Wirklich löschen?');
    expect(dialog.textContent).toContain('Das lässt sich nicht rückgängig machen.');
    expect(q('confirm-ok')!.textContent?.trim()).toBe('Löschen');
    expect(q('confirm-cancel')!.textContent?.trim()).toBe('Abbrechen');
  });

  it('open_MovesFocusIntoTheDialog', async () => {
    await open();

    expect(
      q('confirm-dialog')!.closest('[role="alertdialog"]')!.contains(document.activeElement),
    ).toBe(true);
  });

  it('confirm_Click_EmitsConfirmedAndStaysOpen', async () => {
    await open();

    q('confirm-ok')!.click();
    await stable();

    expect(host.confirmed).toBe(1);
    expect(host.cancelled).toBe(0);
    expect(q('confirm-dialog')).not.toBeNull();
  });

  it('cancel_Click_EmitsCancelledOnceAndCloses', async () => {
    await open();

    q('confirm-cancel')!.click();
    await stable();

    expect(host.cancelled).toBe(1);
    expect(host.confirmed).toBe(0);
    await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());
  });

  it('escape_EmitsCancelledOnceAndCloses', async () => {
    await open();

    q('confirm-dialog')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }),
    );
    await stable();

    expect(host.cancelled).toBe(1);
    await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());
  });

  it('busy_DisablesConfirm', async () => {
    await open();

    host.busy.set(true);
    await stable();
    expect(q<HTMLButtonElement>('confirm-ok')!.disabled).toBe(true);

    host.busy.set(false);
    await stable();
    expect(q<HTMLButtonElement>('confirm-ok')!.disabled).toBe(false);
  });

  it('parentCloses_DoesNotEmitCancelled', async () => {
    await open();

    host.open.set(false);
    await stable();

    expect(host.cancelled).toBe(0);
    await vi.waitFor(() => expect(q('confirm-dialog')).toBeNull());
  });
});

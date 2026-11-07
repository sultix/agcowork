import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpTestingController } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import {
  API,
  COLLECTION_ID,
  SPACE_ID,
  expectRequest,
  pageProviders,
  recordDto,
  settle,
  typeInto,
} from '../../core/testing/test-utils';
import { WorkspaceContext } from '../../core/workspace/workspace-context.service';
import { RecordCreateDialogComponent } from './record-create-dialog.component';
import type { RecordDto } from './record-save.service';

@Component({
  imports: [RecordCreateDialogComponent],
  template: `
    <ag-record-create-dialog
      [collectionId]="collectionId()"
      [spaceId]="spaceId()"
      (created)="created.push($event)"
      (cancelled)="cancelled = cancelled + 1"
    />
  `,
})
class HostComponent {
  readonly collectionId = signal<string | null>(null);
  readonly spaceId = signal<string | null>(SPACE_ID);
  readonly created: RecordDto[] = [];
  cancelled = 0;
}

const template = (id: string, name: string, isDefault: boolean) => ({
  id,
  collectionId: COLLECTION_ID,
  name,
  description: null,
  propertiesPreset: {},
  bodyMarkdown: '',
  isDefault,
  createdBy: 'a',
  createdAt: '2026-11-07T10:00:00Z',
});

describe('RecordCreateDialogComponent', () => {
  let fixture: ComponentFixture<HostComponent>;
  let host: HostComponent;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        ...pageProviders({ routes: [] }),
        provideRouter([]),
        { provide: WorkspaceContext, useValue: { slug: signal('demo') } },
      ],
    });
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(HostComponent);
    host = fixture.componentInstance;
  });

  afterEach(() => http.verify());

  /** The dialog renders into the CDK overlay container at the end of `<body>`. */
  const q = <T extends HTMLElement = HTMLElement>(id: string) =>
    document.body.querySelector<T>(`[data-testid="${id}"]`);

  it('submit_FreePage_PostsTitleAndSpaceWithoutLoadingTemplates', async () => {
    await settle(fixture);
    expect(q('create-template')).toBeNull();
    expect(document.body.querySelector('h2')!.textContent).toContain('Neue Seite');

    typeInto(q<HTMLInputElement>('create-title')!, '  Notizen  ');
    q<HTMLButtonElement>('create-submit')!.click();
    const post = await expectRequest(http, 'POST', `${API}/records`);
    expect(post.request.body).toEqual({ title: 'Notizen', spaceId: SPACE_ID });
    const record = recordDto({ id: 'n1', collectionId: null });
    post.flush(record, { status: 201, statusText: 'Created' });
    await settle(fixture);

    expect(host.created).toEqual([record]);
  });

  it('submit_Collection_OffersTemplatesAndSendsTheChosenOne', async () => {
    host.collectionId.set(COLLECTION_ID);
    fixture.detectChanges();
    (await expectRequest(http, 'GET', `${API}/collections/${COLLECTION_ID}/templates`)).flush([
      template('t1', 'Leer', true),
      template('t2', 'Bug', false),
    ]);
    await settle(fixture);

    typeInto(q<HTMLSelectElement>('create-template')!, 't2');
    typeInto(q<HTMLInputElement>('create-title')!, 'Absturz');
    q<HTMLButtonElement>('create-submit')!.click();
    const post = await expectRequest(http, 'POST', `${API}/records`);

    expect(post.request.body).toEqual({
      title: 'Absturz',
      collectionId: COLLECTION_ID,
      spaceId: SPACE_ID,
      templateId: 't2',
    });
    post.flush(recordDto(), { status: 201, statusText: 'Created' });
    await settle(fixture);
  });

  it('submit_EmptyTitle_ShowsErrorAndSendsNothing', async () => {
    await settle(fixture);

    q<HTMLButtonElement>('create-submit')!.click();
    await settle(fixture);

    expect(q('title-errors')!.textContent).toContain('Bitte einen Titel angeben.');
    expect(host.created).toEqual([]);
  });

  it('submit_Forbidden_ShowsFormErrorAndStaysOpen', async () => {
    await settle(fixture);
    typeInto(q<HTMLInputElement>('create-title')!, 'x');

    q<HTMLButtonElement>('create-submit')!.click();
    (await expectRequest(http, 'POST', `${API}/records`)).flush(
      { code: 'forbidden', status: 403 },
      { status: 403, statusText: 'Forbidden' },
    );
    await settle(fixture);

    expect(q('form-error')!.textContent).toContain('Dafür fehlen die nötigen Rechte');
    expect(q('create-record-dialog')).not.toBeNull();
  });

  it('submit_Retry_ReusesTheIdempotencyKeyAndNewDialogGetsANewOne', async () => {
    await settle(fixture);
    typeInto(q<HTMLInputElement>('create-title')!, 'x');

    q<HTMLButtonElement>('create-submit')!.click();
    const first = await expectRequest(http, 'POST', `${API}/records`);
    const key = first.request.headers.get('Idempotency-Key');
    expect(key).toMatch(/^[0-9a-f-]{36}$/);
    first.error(new ProgressEvent('error'));
    await settle(fixture);

    q<HTMLButtonElement>('create-submit')!.click();
    const second = await expectRequest(http, 'POST', `${API}/records`);
    expect(second.request.headers.get('Idempotency-Key')).toBe(key);
    second.flush(recordDto(), { status: 201, statusText: 'Created' });
    await settle(fixture);
    fixture.destroy();

    const other = TestBed.createComponent(HostComponent);
    await settle(other);
    typeInto(q<HTMLInputElement>('create-title')!, 'y');
    q<HTMLButtonElement>('create-submit')!.click();
    const third = await expectRequest(http, 'POST', `${API}/records`);
    expect(third.request.headers.get('Idempotency-Key')).not.toBe(key);
    third.flush(recordDto(), { status: 201, statusText: 'Created' });
    await settle(other);
  });

  it('cancel_ButtonAndEscape_EmitCancelled', async () => {
    await settle(fixture);

    q<HTMLButtonElement>('create-cancel')!.click();
    q('create-record-dialog')!.dispatchEvent(
      new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }),
    );
    await settle(fixture);

    expect(host.cancelled).toBe(2);
  });
});

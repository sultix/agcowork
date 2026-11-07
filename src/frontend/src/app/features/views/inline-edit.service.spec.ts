import { EnvironmentInjector, createEnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { apiErrorInterceptor } from '../../core/api/api-error.interceptor';
import { API, RECORD_ID, recordDto } from '../../core/testing/test-utils';
import type { RecordDto } from '../records/record-save.service';
import { TITLE_KEY } from '../../shared/table/inline-edit';
import { InlineEditService, RowStore } from './inline-edit.service';

const URL = `${API}/records/${RECORD_ID}`;
const conflictBody = {
  code: 'revision_mismatch',
  status: 409,
  currentRevision: 7,
  changedKeys: ['status'],
};

describe('InlineEditService', () => {
  let http: HttpTestingController;
  let service: InlineEditService;
  let rows: RecordDto[];
  let injector: EnvironmentInjector;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([apiErrorInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpTestingController);
    injector = createEnvironmentInjector([InlineEditService], TestBed.inject(EnvironmentInjector));
    service = injector.get(InlineEditService);
    rows = [recordDto({ revision: 3, properties: { status: 'todo', estimate: 2 } as never })];
    const store: RowStore = {
      find: (id) => rows.find((r) => r.id === id),
      replace: (record) => (rows = rows.map((r) => (r.id === record.id ? record : r))),
    };
    service.bind(() => 'demo', store);
  });

  afterEach(() => {
    injector.destroy();
    http.verify();
  });

  /** Saves start one microtask later (they are queued per record). */
  const pending = async (): Promise<HttpTestingController> => {
    await new Promise((resolve) => setTimeout(resolve));
    return http;
  };
  const row = () => rows[0];
  const props = () => row().properties as Record<string, unknown>;

  it('save_Property_SendsOnlyThatKeyWithExpectedRevisionAndAdoptsServerRecord', async () => {
    const saving = service.save(row(), 'status', 'done');

    expect(props()['status']).toBe('done'); // optimistic
    expect(service.cell(RECORD_ID, 'status')?.status).toBe('saving');
    const request = (await pending()).expectOne({ method: 'PATCH', url: URL });
    expect(request.request.body).toEqual({ properties: { status: 'done' }, expectedRevision: 3 });
    request.flush(recordDto({ revision: 4, properties: { status: 'done', estimate: 2 } as never }));

    expect(await saving).toBe(true);
    expect(row().revision).toBe(4);
    expect(service.cell(RECORD_ID, 'status')).toBeUndefined();
  });

  it('save_Title_SendsTitle', async () => {
    const saving = service.save(row(), TITLE_KEY, 'Neuer Titel');

    const request = (await pending()).expectOne(URL);
    expect(request.request.body).toEqual({ title: 'Neuer Titel', expectedRevision: 3 });
    request.flush(recordDto({ revision: 4, title: 'Neuer Titel' }));
    expect(await saving).toBe(true);
    expect(row().title).toBe('Neuer Titel');
  });

  it('save_TwoEditsOfOneRecord_SecondWaitsAndUsesNewRevision', async () => {
    const first = service.save(row(), 'status', 'done');
    const second = service.save(row(), 'estimate', 5);

    const one = (await pending()).expectOne(URL);
    expect(one.request.body).toEqual({ properties: { status: 'done' }, expectedRevision: 3 });
    one.flush(recordDto({ revision: 4, properties: { status: 'done', estimate: 2 } as never }));
    await first;
    await new Promise((r) => setTimeout(r));
    const two = (await pending()).expectOne(URL);
    expect(two.request.body).toEqual({ properties: { estimate: 5 }, expectedRevision: 4 });
    two.flush(recordDto({ revision: 5, properties: { status: 'done', estimate: 5 } as never }));

    expect(await second).toBe(true);
    expect(row().revision).toBe(5);
    expect(props()).toEqual({ status: 'done', estimate: 5 });
  });

  it('save_Conflict_MarksCellKeepsValueAndOffersDialog', async () => {
    const saving = service.save(row(), 'status', 'done');
    (await pending()).expectOne(URL).flush(conflictBody, { status: 409, statusText: 'Conflict' });

    expect(await saving).toBe(false);
    expect(service.cell(RECORD_ID, 'status')?.status).toBe('conflict');
    expect(service.conflict()).toEqual({
      recordId: RECORD_ID,
      key: 'status',
      conflict: { currentRevision: 7, changedKeys: ['status'] },
    });
    expect(props()['status']).toBe('done');
  });

  it('conflict_Reload_TakesServerStateAndClearsMark', async () => {
    const saving = service.save(row(), 'status', 'done');
    (await pending()).expectOne(URL).flush(conflictBody, { status: 409, statusText: 'Conflict' });
    await saving;

    const reloading = service.reloadConflict();
    const get = (await pending()).expectOne({ method: 'GET', url: URL });
    get.flush(recordDto({ revision: 7, properties: { status: 'todo', estimate: 9 } as never }));
    await reloading;

    expect(service.conflict()).toBeNull();
    expect(service.cell(RECORD_ID, 'status')).toBeUndefined();
    expect(row().revision).toBe(7);
    expect(props()).toEqual({ status: 'todo', estimate: 9 });
  });

  it('conflict_Overwrite_SendsWithoutExpectedRevisionAndNoBodyFetch', async () => {
    const saving = service.save(row(), 'status', 'done');
    (await pending()).expectOne(URL).flush(conflictBody, { status: 409, statusText: 'Conflict' });
    await saving;

    const overwriting = service.overwriteConflict();
    const patch = (await pending()).expectOne({ method: 'PATCH', url: URL });
    expect(patch.request.body).toEqual({ properties: { status: 'done' } });
    patch.flush(recordDto({ revision: 8, properties: { status: 'done', estimate: 9 } as never }));
    await overwriting;

    http.expectNone({ method: 'GET', url: `${URL}?includeBody=true` });
    expect(service.conflict()).toBeNull();
    expect(row().revision).toBe(8);
    expect(service.cell(RECORD_ID, 'status')).toBeUndefined();
  });

  it('save_Rollback_ConflictRestoresPreviousValue', async () => {
    const saving = service.save(row(), 'status', 'done', { rollback: true });
    (await pending()).expectOne(URL).flush(conflictBody, { status: 409, statusText: 'Conflict' });

    expect(await saving).toBe(false);
    expect(props()['status']).toBe('todo');
    expect(service.conflict()).not.toBeNull();
  });

  it('save_ValidationError_RollsBackAndKeepsMessageOnCell', async () => {
    const saving = service.save(row(), 'estimate', -1);
    (await pending()).expectOne(URL).flush(
      {
        code: 'validation_failed',
        status: 400,
        errors: [{ field: 'properties.estimate', code: 'out_of_range', message: 'x' }],
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(await saving).toBe(false);
    expect(props()['estimate']).toBe(2);
    expect(service.cell(RECORD_ID, 'estimate')).toEqual({
      status: 'error',
      message: 'Wert liegt außerhalb des erlaubten Bereichs.',
      draft: -1,
    });

    // the next edit starts fresh from the row (no stale dirty key)
    const next = service.save(row(), 'status', 'done');
    const request = (await pending()).expectOne(URL);
    expect(request.request.body).toEqual({ properties: { status: 'done' }, expectedRevision: 3 });
    request.flush(recordDto({ revision: 4 }));
    expect(await next).toBe(true);
  });

  it('save_ArchivedMeanwhile_RowBecomesArchived', async () => {
    const saving = service.save(row(), 'status', 'done');
    (await pending())
      .expectOne(URL)
      .flush({ code: 'record_archived', status: 409 }, { status: 409, statusText: 'Conflict' });
    await new Promise((r) => setTimeout(r));
    (await pending())
      .expectOne({ method: 'GET', url: URL })
      .flush(recordDto({ archivedAt: '2026-11-07T12:00:00Z' }));

    expect(await saving).toBe(false);
    expect(row().archivedAt).toBe('2026-11-07T12:00:00Z');
    expect(props()['status']).toBe('todo');
    expect(service.cell(RECORD_ID, 'status')?.message).toContain('archiviert');
  });
  it('save_ValidationErrorWhileNextEditWaits_NextSendsOnlyItsOwnKey', async () => {
    const first = service.save(row(), 'estimate', -1);
    const second = service.save(row(), 'status', 'done');

    (await pending()).expectOne(URL).flush(
      {
        code: 'validation_failed',
        status: 400,
        errors: [{ field: 'properties.estimate', code: 'out_of_range', message: 'x' }],
      },
      { status: 400, statusText: 'Bad Request' },
    );
    expect(await first).toBe(false);
    await new Promise((r) => setTimeout(r));
    const next = (await pending()).expectOne(URL);
    expect(next.request.body).toEqual({ properties: { status: 'done' }, expectedRevision: 3 });
    next.flush(recordDto({ revision: 4, properties: { status: 'done', estimate: 2 } as never }));

    expect(await second).toBe(true);
    expect(props()).toEqual({ status: 'done', estimate: 2 });
    expect(service.cell(RECORD_ID, 'estimate')?.draft).toBe(-1);
  });

  it('save_WhileEarlierEditRuns_KeepsBothValuesVisible', async () => {
    const first = service.save(row(), 'status', 'done');
    const second = service.save(row(), 'estimate', 5);

    (await pending())
      .expectOne(URL)
      .flush(recordDto({ revision: 4, properties: { status: 'done', estimate: 2 } as never }));
    await first;
    expect(props()).toEqual({ status: 'done', estimate: 5 }); // the queued value is still shown
    await new Promise((r) => setTimeout(r));
    (await pending())
      .expectOne(URL)
      .flush(recordDto({ revision: 5, properties: { status: 'done', estimate: 5 } as never }));
    expect(await second).toBe(true);
  });

  it('save_AfterRowsWereRefreshed_UsesTheRowRevision', async () => {
    const first = service.save(row(), 'status', 'done');
    (await pending())
      .expectOne(URL)
      .flush(recordDto({ revision: 4, properties: { status: 'done' } as never }));
    await first;
    rows = [recordDto({ revision: 9, properties: { status: 'done' } as never })]; // e.g. reloaded

    const second = service.save(row(), 'status', 'todo');
    const request = (await pending()).expectOne(URL);
    expect(request.request.body).toEqual({ properties: { status: 'todo' }, expectedRevision: 9 });
    request.flush(recordDto({ revision: 10, properties: { status: 'todo' } as never }));
    expect(await second).toBe(true);
  });
});

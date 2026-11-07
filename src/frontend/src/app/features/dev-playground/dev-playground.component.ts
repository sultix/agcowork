import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { MarkdownEditorComponent } from '../../shared/editor/markdown-editor.component';
import { RawViewComponent } from '../../shared/raw-view/raw-view.component';
import { PLAYGROUND_JSON, PLAYGROUND_MARKDOWN } from './playground-samples';

/**
 * Manual test bench for the editor building blocks (`/dev/playground`, not linked in the UI).
 * The Markdown editor and the Markdown raw view share one signal, so edits flow both ways.
 */
@Component({
  selector: 'ag-dev-playground',
  imports: [MarkdownEditorComponent, RawViewComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <main class="mx-auto flex max-w-7xl flex-col gap-4 p-6">
      <header class="flex flex-wrap items-start justify-between gap-4">
        <h1 class="text-2xl font-semibold tracking-tight">Editor-Spielwiese</h1>
        <span class="text-sm text-muted-foreground">{{ markdown().length }} Zeichen Markdown</span>
      </header>
      <div class="grid gap-4 lg:grid-cols-2">
        <section class="flex flex-col gap-2" aria-label="Markdown-Editor">
          <h2 class="text-sm font-medium text-muted-foreground">WYSIWYG</h2>
          <ag-markdown-editor
            class="min-h-[28rem]"
            placeholder="Schreib etwas …"
            [value]="markdown()"
            (valueChange)="markdown.set($event)"
          />
        </section>
        <section class="flex flex-col gap-2" aria-label="Markdown-Quelltext">
          <h2 class="text-sm font-medium text-muted-foreground">Markdown (Raw)</h2>
          <ag-raw-view
            class="h-[28rem]"
            language="markdown"
            [value]="markdown()"
            (valueChange)="markdown.set($event)"
          />
        </section>
      </div>
      <section class="flex flex-col gap-2" aria-label="JSON-Quelltext">
        <h2 class="text-sm font-medium text-muted-foreground">JSON (Raw)</h2>
        <ag-raw-view
          class="h-72"
          language="json"
          [value]="json()"
          (valueChange)="json.set($event)"
        />
      </section>
    </main>
  `,
})
export class DevPlaygroundComponent {
  protected readonly markdown = signal(PLAYGROUND_MARKDOWN);
  protected readonly json = signal(PLAYGROUND_JSON);
}

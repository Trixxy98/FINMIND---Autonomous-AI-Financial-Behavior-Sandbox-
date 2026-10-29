import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const html = await readFile(new URL('../index.html', import.meta.url), 'utf8');

describe('web scaffold', () => {
  it('declares the FINMIND entry document and application mount point', () => {
    assert.match(html, /<title>FINMIND<\/title>/);
    assert.match(html, /id="root"/);
    assert.match(html, /src="\/src\/main\.tsx"/);
  });
});

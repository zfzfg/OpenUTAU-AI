import { build } from 'esbuild';
import { mkdir, readFile, writeFile, copyFile, readdir } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { join } from 'node:path';
const out='dist';await mkdir(out,{recursive:true});
await build({entryPoints:{'mcp-server':'packages/mcp-server/index.ts','agent-host':'packages/agent-host/index.ts','assistant-tools':'packages/assistant-tools/index.ts'},outdir:out,bundle:true,platform:'node',format:'esm',target:'node24',sourcemap:false,banner:{js:"import { createRequire as __createRequire } from 'node:module'; const require = __createRequire(import.meta.url);"},legalComments:'external'});
// Development testing exports the real production modules without triggering stdio entry points.
await build({entryPoints:{'test-library':'test/library.ts'},outdir:out,bundle:true,platform:'node',format:'esm',target:'node24',banner:{js:"import { createRequire as __createRequire } from 'node:module'; const require = __createRequire(import.meta.url);"},legalComments:'external'});
await writeFile(join(out,'package.json'),JSON.stringify({type:'module',name:'openutau-ai-runtime',version:'1.0.0'}));
await mkdir(join(out,'contracts'),{recursive:true});for(const name of (await readdir('../../contracts/automation/v1')).filter(x=>x.endsWith('.json')))await copyFile(join('../../contracts/automation/v1',name),join(out,'contracts',name));
const artifacts={};for(const name of ['mcp-server.js','agent-host.js','assistant-tools.js',...(await readdir(join(out,'contracts'))).filter(x=>x.endsWith('.json')).map(x=>`contracts/${x}`)])artifacts[name]={sha256:createHash('sha256').update(await readFile(join(out,name))).digest('hex')};
await writeFile(join(out,'manifest.json'),JSON.stringify({schemaVersion:1,apiVersion:'1',hostVersion:'1.0.0',mcpSdk:{package:'@modelcontextprotocol/server',version:'2.3.1',protocol:'2026-07-28'},acpProtocol:1,nodeMinimum:'24',artifacts},null,2)+'\n');

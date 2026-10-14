import test from 'node:test';import assert from 'node:assert/strict';
import { Client } from '@modelcontextprotocol/client';import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { resolve,join } from 'node:path';import { rm,readFile,writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import { fakeEditor,privateWorkspace } from './support.mjs';
import { discover,nativeGrant,privateWrite,EditorClient } from '../dist/test-library.js';
async function connect(env){const client=new Client({name:'openutau-standard-client-test',version:'1.0'},{capabilities:{}});const transport=new StdioClientTransport({command:process.execPath,args:[resolve('dist/mcp-server.js'),'--stdio'],env:{...process.env,...env},stderr:'pipe'});await client.connect(transport);return {client,transport};}
test('standard SDK client initializes and lists every tool without editor, sidebar, CLI or host',async()=>{
  const folder=await privateWorkspace();const {client}=await connect({OPENUTAU_AUTOMATION_DIR:folder});
  try{const {tools}=await client.listTools();assert.equal(tools.length,27);assert.ok(tools.every(x=>x.inputSchema.additionalProperties===false));const result=await client.callTool({name:'openutau_get_project',arguments:{}});assert.equal(result.isError,true);assert.equal(result.structuredContent.error.code,'APP_NOT_RUNNING');assert.deepEqual(JSON.parse(result.content[0].text),result.structuredContent);}
  finally{await client.close();await rm(folder,{recursive:true,force:true});}
});
test('external SDK client obtains its own grant and submits validated editor transactions',async()=>{
  const editor=await fakeEditor();const {client}=await connect({OPENUTAU_AUTOMATION_DIR:editor.folder});
  try{
    const list=await client.callTool({name:'openutau_list_instances',arguments:{}});assert.equal(list.structuredContent.instances.length,1);assert.ok(!JSON.stringify(list).includes('credential_'));
    const project=await client.callTool({name:'openutau_get_project',arguments:{}});assert.equal(project.structuredContent.editRevision,7);
    const edit=await client.callTool({name:'openutau_apply_edit_batch',arguments:{projectSessionId:'project_fixture',expectedEditRevision:7,operationId:'operation_one',operations:[{type:'set_lyrics',partId:'part',items:[{noteId:'note',lyric:'hello'}]}]}});assert.equal(edit.isError,false);assert.equal(edit.structuredContent.status,'committed');
    const invalid=await client.callTool({name:'openutau_apply_edit_batch',arguments:{projectSessionId:'project_fixture',expectedEditRevision:7,operationId:'operation_invalid',operations:[{type:'eval',code:'arbitrary'}]}});assert.equal(invalid.structuredContent.error.code,'INVALID_ARGUMENT');
    assert.equal(editor.requests.filter(x=>x.url.endsWith('/invoke')).length,2);assert.equal(editor.requests[1].body.apiVersion,'1');
  }finally{await client.close();await editor.dispose();}
});
test('ambiguous editor choice is explicit; duplicate descriptors never establish trust',async()=>{
  const first=await fakeEditor();const second=await fakeEditor({instanceId:'instance_second'});
  try{
    const descriptor=JSON.parse(await readFile(join(second.folder,'instance_second.json'),'utf8'));await privateWrite(join(first.folder,'second.json'),descriptor);
    const client=new EditorClient({OPENUTAU_AUTOMATION_DIR:first.folder});await assert.rejects(client.invoke('get_project',{}),x=>x.code==='INSTANCE_AMBIGUOUS');
    assert.equal((await client.invoke('get_project',{instanceId:'instance_second'})).ok,true);
    await privateWrite(join(first.folder,'duplicate.json'),descriptor);assert.equal((await discover({OPENUTAU_AUTOMATION_DIR:first.folder})).length,1);
  }finally{await first.dispose();await second.dispose();}
});
test('invalid native grant never falls back to external discovery permissions',async()=>{
  const editor=await fakeEditor();try{
    const client=new EditorClient({OPENUTAU_AUTOMATION_DIR:editor.folder,OPENUTAU_ACCESS_GRANT_REF:join(editor.folder,'missing')});await assert.rejects(client.invoke('get_project',{}),x=>x.code==='UNAUTHORIZED');assert.equal(editor.requests.length,0);
    await assert.rejects(nativeGrant(editor.grantRef,{OPENUTAU_INSTANCE_ID:'other'}),x=>x.code==='UNAUTHORIZED');
    const value=JSON.parse(await readFile(editor.grantRef,'utf8'));value.url='http://evil.test';await writeFile(editor.grantRef,JSON.stringify(value));await assert.rejects(nativeGrant(editor.grantRef),x=>x.code==='UNAUTHORIZED');
  }finally{await editor.dispose();}
});
test('explicit instance grants avoid rediscovery; only get_project can bind a changed project',async()=>{
  const editor=await fakeEditor();try{
    const client=new EditorClient({OPENUTAU_AUTOMATION_DIR:editor.folder,OPENUTAU_INSTANCE_ID:editor.instanceId});
    await client.invoke('get_project',{});const firstHealth=editor.healthChecks;await client.invoke('get_capabilities',{});assert.equal(editor.healthChecks,firstHealth);
    editor.setProjectSession('project_replaced');const stale={projectSessionId:'project_fixture',expectedEditRevision:7,operationId:'stale_write',operations:[{type:'set_project_info',name:'Must not apply'}]};
    const failed=await client.invoke('apply_edit_batch',stale);assert.equal(failed.error.code,'PROJECT_CHANGED');assert.equal(editor.requests.filter(x=>x.body.operation==='apply_edit_batch').length,1);
    const read=await client.invoke('get_project',{});assert.equal(read.projectSessionId,'project_replaced');
    await assert.rejects(client.invoke('apply_edit_batch',stale),x=>x.code==='PROJECT_CHANGED');assert.equal(editor.requests.filter(x=>x.body.operation==='apply_edit_batch').length,1);
  }finally{await editor.dispose();}
});
test('bundles manifest hashes the delivered executables',async()=>{
  const manifest=JSON.parse(await readFile('dist/manifest.json','utf8'));for(const [name,entry] of Object.entries(manifest.artifacts))assert.equal(createHash('sha256').update(await readFile(join('dist',name))).digest('hex'),entry.sha256);
});

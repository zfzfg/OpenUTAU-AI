// Invoked by the C# Kestrel integration test against its actual editor service.
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { stat } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
const [instanceId,outputPath]=process.argv.slice(2);
if(!instanceId||!outputPath)throw Error('Usage: node test/editor-roundtrip.mjs instanceId absolute-output.ustx');
const client=new Client({name:'openutau-real-editor-test',version:'1.0.0'},{capabilities:{}});
const transport=new StdioClientTransport({command:process.execPath,args:[fileURLToPath(new URL('../dist/mcp-server.js',import.meta.url)),'--stdio'],env:{...process.env,OPENUTAU_INSTANCE_ID:instanceId,OPENUTAU_CLIENT_ID:`real-editor-test_${randomUUID()}`},stderr:'pipe'});
await client.connect(transport);
async function call(name,args={}){const response=await client.callTool({name:`openutau_${name}`,arguments:{instanceId,...args}});const value=response.structuredContent||JSON.parse(response.content[0].text);if(response.isError||value.ok!==true)throw Error(JSON.stringify(value));return value;}
try{
  const tools=(await client.listTools()).tools;assert.equal(tools.length,27);
  const listed=await call('list_instances');assert.ok(listed.instances.some(x=>x.instanceId===instanceId));
  const project=await call('get_project');assert.ok(project.projectSessionId);assert.ok(Number.isSafeInteger(project.editRevision));
  const operationId=randomUUID();const edit={projectSessionId:project.projectSessionId,expectedEditRevision:project.editRevision,operationId,label:'MCP integration song',operations:[
    {type:'set_project_info',name:'MCP integrated song'},
    {type:'add_track',clientRef:'track-a',name:'Vocal'},
    {type:'add_part',clientRef:'part-a',trackId:'track-a',name:'Verse',positionTicks:0,durationTicks:1920},
    {type:'add_notes',partId:'part-a',notes:[{clientRef:'note-a',positionTicks:0,durationTicks:480,tone:60,lyric:'hello'},{clientRef:'note-b',positionTicks:480,durationTicks:720,tone:64,lyric:'+'}]},
    {type:'add_part',clientRef:'part-b',trackId:'track-a',name:'Chorus',positionTicks:1920,durationTicks:1920},
    {type:'add_notes',partId:'part-b',notes:[{positionTicks:0,durationTicks:960,tone:67,lyric:'world'}]}
  ]};
  const preview=await call('preview_edit',edit);assert.equal(preview.editRevision,project.editRevision);
  const changed=await call('apply_edit_batch',edit);assert.equal(changed.status,'committed');assert.ok(changed.editRevision>project.editRevision);
  const repeated=await call('apply_edit_batch',edit);assert.equal(repeated.editRevision,changed.editRevision);
  const journal=await call('get_operation',{operationId});assert.equal(journal.status,'committed');
  const after=await call('get_project');const part=after.parts?.find(x=>x.name==='Verse')||after.project?.parts?.find(x=>x.name==='Verse');
  assert.ok(part,'Get project must expose created parts');
  const partId=part.partId||part.id;const page=await call('get_notes',{partId,limit:1});assert.equal(page.items.length,1);assert.ok(page.nextCursor);
  const next=await call('get_notes',{partId,limit:1,cursor:page.nextCursor});assert.equal(next.items.length,1);assert.equal(next.snapshotId,page.snapshotId);assert.equal(next.editRevision,page.editRevision);assert.equal(next.items[0].lyric,'+');
  const target=await call('resolve_output_target',{path:outputPath,purpose:'project',overwrite:false});assert.ok(target.outputHandle);
  const saved=await call('save_project',{projectSessionId:after.projectSessionId,expectedEditRevision:after.editRevision,operationId:randomUUID(),outputHandle:target.outputHandle,overwrite:false});assert.ok((await stat(outputPath)).size>0);
  process.stdout.write(JSON.stringify({ok:true,tools:tools.length,projectSessionId:after.projectSessionId,editRevision:after.editRevision,notes:page.items.length+next.items.length,parts:2,saved:outputPath,operationId})+'\n');
}finally{await client.close();}

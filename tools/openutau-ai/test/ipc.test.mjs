import test from 'node:test';import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';import { resolve } from 'node:path';
import { readFile } from 'node:fs/promises';
import Ajv2020 from 'ajv/dist/2020.js';
import { RpcPeer,stopOwnedProcess,AgentHost } from '../dist/test-library.js';
import { fakeEditor,fakeAgent,until } from './support.mjs';
test('real bundled host stdin/stdout matches native bridge request/notification fields and schema',async()=>{
  const editor=await fakeEditor();const child=spawn(process.execPath,[resolve('dist/agent-host.js'),'--stdio'],{cwd:process.cwd(),stdio:['pipe','pipe','pipe'],windowsHide:true,detached:process.platform!=='win32'});
  const peer=new RpcPeer(child.stdout,child.stdin);const events=[];const diagnostics=[];child.stderr.on('data',chunk=>diagnostics.push(chunk.toString()));
  const ajv=new Ajv2020({strict:false,allErrors:true});const validate=ajv.compile(JSON.parse(await readFile('../../contracts/automation/v1/agent-host.schema.json','utf8')));
  const invalid=[];peer.onNotification=(method,params)=>{const frame=JSON.parse(JSON.stringify({jsonrpc:'2.0',method,params}));if(!validate(frame))invalid.push({frame,errors:validate.errors});events.push({method,...params});};
  try{
    const init=await peer.request('initialize',{apiVersion:'1',appVersion:'1.0.0',processGeneration:1});assert.equal(init.processGeneration,1);
    const created=await peer.request('session.create',{providerId:'codex',cwd:process.cwd(),cliPath:process.execPath,adapterPath:fakeAgent,instanceId:editor.instanceId,projectSessionId:'project_fixture',accessGrantRef:editor.grantRef,clientId:'native_bridge_fixture',mode:'apply'});assert.equal(created.processGeneration,1);
    const prompt=peer.request('session.prompt',{sessionId:created.sessionId,text:'permission',context:{projectSessionId:'project_fixture',editRevision:7}},0);const card=await until(()=>events.find(x=>x.method==='permissionRequested'));
    assert.equal(card.sessionId,created.sessionId);assert.ok(card.turnId);await peer.request('permission.respond',{sessionId:card.sessionId,turnId:card.turnId,processGeneration:card.processGeneration,requestId:card.requestId,optionId:card.options[0].optionId});await prompt;
    const waiting=peer.request('session.prompt',{sessionId:created.sessionId,text:'wait'},0);await until(()=>events.filter(x=>x.state==='working').length>=3);peer.notify('session.cancel',{sessionId:created.sessionId});assert.equal((await waiting).stopReason,'cancelled');
    await peer.request('session.dispose',{sessionId:created.sessionId});await peer.request('shutdown',{});assert.deepEqual(diagnostics,[]);assert.deepEqual(invalid,[]);
  }finally{await stopOwnedProcess(child);peer.close();await editor.dispose();}
});
test('Grok native interview returns required outcome and maps offered labels exactly once',async()=>{
  const editor=await fakeEditor();const events=[];const host=new AgentHost((method,params)=>events.push({method,...params}),resolve('dist'));
  try{
    await host.dispatch('initialize',{apiVersion:'1',processGeneration:1});const created=await host.dispatch('session.create',{providerId:'grok',cwd:process.cwd(),cliPath:fakeAgent,instanceId:editor.instanceId,projectSessionId:'project_fixture',accessGrantRef:editor.grantRef});
    const prompt=host.dispatch('session.prompt',{sessionId:created.sessionId,text:'grok-interview'});const cards=await until(()=>events.filter(x=>x.method==='questionRequested').length===2&&events.filter(x=>x.method==='questionRequested'));
    for(const card of cards)await host.dispatch('question.respond',{...card,optionId:'0'});const result=await prompt;const answer=JSON.parse(result.text);assert.equal(answer.outcome,'accepted');assert.deepEqual(answer.answers,{'Language?':'German','Tempo?':'120'});
  }finally{try{await host.dispose();}finally{await editor.dispose();}}
});

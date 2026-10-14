import test from 'node:test';import assert from 'node:assert/strict';
import { PassThrough } from 'node:stream';import { resolve } from 'node:path';
import { AgentHost,injectMcp,JsonLineReader,sessionCatalog } from '../dist/test-library.js';
import { fakeEditor,fakeAgent,until } from './support.mjs';
async function hostFixture(providerId='codex',nativeSessionId){const editor=await fakeEditor();const events=[];const host=new AgentHost((method,params)=>events.push({method,...params}),resolve('dist'));try{await host.dispatch('initialize',{apiVersion:'1',processGeneration:1});const created=await host.dispatch(nativeSessionId?'session.load':'session.create',{providerId,cwd:process.cwd(),cliPath:providerId==='grok'?fakeAgent:process.execPath,adapterPath:fakeAgent,instanceId:editor.instanceId,projectSessionId:'project_fixture',accessGrantRef:editor.grantRef,nativeSessionId});return {editor,events,host,created,async dispose(){try{await host.dispose();}finally{await editor.dispose();}}};}catch(error){try{await host.dispose();}finally{await editor.dispose();}throw error;}}
for(const providerId of ['codex','claude','grok'])test(`${providerId} ACP backend: v1 new/load injection, dynamic catalog and UTF-8 streaming`,async()=>{
  const fixture=await hostFixture(providerId);try{
    const {host,created}=fixture;assert.equal(created.models.length,2);assert.deepEqual(created.efforts.map(x=>x.id),['low','high']);
    await assert.rejects(host.dispatch('session.setModel',{sessionId:created.sessionId,modelId:'imaginary'}),x=>x.code==='INVALID_ARGUMENT');
    await assert.rejects(host.dispatch('session.setEffort',{sessionId:created.sessionId,effortId:'imaginary'}),x=>x.code==='INVALID_ARGUMENT');
    const changed=await host.dispatch('session.setEffort',{sessionId:created.sessionId,effortId:'high'});assert.equal(changed.currentEffortId,'high');
    const result=await host.dispatch('session.prompt',{sessionId:created.sessionId,text:'hello'});assert.equal(result.text,'Hallo 🎵 Grüße');assert.equal(fixture.events.filter(x=>x.method==='turnCompleted').length,1);
    const history=await host.dispatch('session.list',{sessionId:created.sessionId});assert.equal(history.sessions.length,1);
    const resumed=await hostFixture(providerId,created.nativeSessionId);try{assert.equal(resumed.created.nativeSessionId,created.nativeSessionId);assert.ok(resumed.events.some(x=>x.method==='messageDelta'&&x.text==='Resumed conversation.'));}finally{await resumed.dispose();}
  }finally{await fixture.dispose();}
});
test('real MCP tool round through fake ACP shows actual structured editor result',async()=>{
  const fixture=await hostFixture();try{await fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:'tool'});const card=fixture.events.find(x=>x.method==='toolCompleted');assert.equal(card.result.ok,true);assert.equal(card.result.editRevision,7);assert.equal(fixture.editor.requests.filter(x=>x.url.endsWith('/invoke')).length,1);}finally{await fixture.dispose();}
});
test('permission responds exactly once and rejects stale turn and generation',async()=>{
  const fixture=await hostFixture();try{
    const promise=fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:'permission'});const card=await until(()=>fixture.events.find(x=>x.method==='permissionRequested'));
    await assert.rejects(fixture.host.dispatch('permission.respond',{...card,processGeneration:2,optionId:'allow_once'}),x=>x.code==='REQUEST_STALE');
    await assert.rejects(fixture.host.dispatch('permission.respond',{...card,optionId:'invented'}),x=>x.code==='INVALID_ARGUMENT');
    assert.equal((await fixture.host.dispatch('permission.respond',{...card,optionId:'allow_once'})).accepted,true);
    await assert.rejects(fixture.host.dispatch('permission.respond',{...card,optionId:'allow_once'}),x=>x.code==='REQUEST_STALE');await promise;
    assert.equal(fixture.events.filter(x=>x.method==='requestWithdrawn').length,1);
  }finally{await fixture.dispose();}
});
test('Codex helper MCP question receives a native response through the authenticated host service',async()=>{
  const fixture=await hostFixture();try{
    const prompt=fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:'helper-question'});const card=await until(()=>fixture.events.find(x=>x.method==='questionRequested'),10000);
    const answered=await fixture.host.dispatch('question.respond',{...card,optionId:'yes'});assert.equal(answered.accepted,true);const reply=await prompt;assert.ok(reply.text.includes('answered'));assert.ok(reply.text.includes('yes'));
    await assert.rejects(fixture.host.dispatch('question.respond',{...card,optionId:'yes'}),x=>x.code==='REQUEST_STALE');
  }finally{await fixture.dispose();}
});
for(const scenario of ['wait','permission','native-question'])test(`cancel ${scenario} withdraws cards and sends notification without ACP ID`,async()=>{
  const fixture=await hostFixture(scenario==='native-question'?'grok':'codex');try{
    const prompt=fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:scenario});await until(()=>fixture.events.find(x=>x.state===(scenario==='wait'?'working':'waiting_user')));
    await fixture.host.dispatch('session.cancel',{sessionId:fixture.created.sessionId});await prompt;assert.ok(fixture.events.some(x=>x.method==='turnCompleted'));assert.equal(fixture.events.at(-1).state,'ready');
  }finally{await fixture.dispose();}
});
for(const scenario of ['stream-wait','tool-wait','helper-question'])test(`stop while ${scenario} settles stream/tool/user waits`,async()=>{
  const fixture=await hostFixture();try{
    const prompt=fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:scenario});const wanted=scenario==='stream-wait'?'messageDelta':scenario==='tool-wait'?'toolStarted':'questionRequested';await until(()=>fixture.events.find(x=>x.method===wanted),10000);
    await fixture.host.dispatch('session.cancel',{sessionId:fixture.created.sessionId});await prompt;assert.ok(fixture.events.some(x=>x.method==='turnCompleted'));if(scenario==='helper-question')assert.ok(fixture.events.some(x=>x.method==='requestWithdrawn'&&x.reason==='cancelled'));
  }finally{await fixture.dispose();}
});
test('unconfirmed cancel escalates only the owned provider process and verifies exit',async()=>{
  const fixture=await hostFixture();try{const prompt=fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:'ignore_cancel'}).catch(error=>error);await until(()=>fixture.events.find(x=>x.state==='working'));const result=await fixture.host.dispatch('session.cancel',{sessionId:fixture.created.sessionId});assert.equal(result.processTerminated,true);await prompt;assert.ok(fixture.events.some(x=>x.method==='processExited'));}finally{await fixture.dispose();}
});
for(const scenario of ['malformed','oversize','eof'])test(`provider ${scenario} fails visibly and cleans up pending turn`,async()=>{const fixture=await hostFixture();try{await assert.rejects(fixture.host.dispatch('session.prompt',{sessionId:fixture.created.sessionId,text:scenario}));assert.ok(fixture.events.some(x=>x.method==='error'));}finally{await fixture.dispose();}});
test('JSONL rejects oversized frames and preserves Unicode across byte boundaries',()=>{
  const input=new PassThrough();const frames=[];const errors=[];new JsonLineReader(input,x=>frames.push(x),x=>errors.push(x),()=>{},100);const text=Buffer.from(JSON.stringify({jsonrpc:'2.0',method:'delta',params:{text:'🎵'}})+'\n');const boundary=text.indexOf(Buffer.from('🎵'))+1;input.write(text.subarray(0,boundary));input.write(text.subarray(boundary));assert.equal(frames[0].params.text,'🎵');input.write('a'.repeat(101));assert.equal(errors.length,1);
});
test('MCP injection is stable on repeated resume and collisions fail closed',()=>{
  const config={name:'openutau',command:'node',args:['mcp-server.js'],env:[]};const first=injectMcp([],config);assert.deepEqual(injectMcp(first,config),first);assert.throws(()=>injectMcp([{...config,command:'different'}],config),x=>x.code==='MCP_CONFIG_COLLISION');
});
test('Claude config option catalogs expose only reported model and effort values',()=>{
  const catalog=sessionCatalog({configOptions:[{id:'model',category:'model',currentValue:'catalog-model',options:[{value:'catalog-model',name:'Reported'}]},{id:'effort',category:'thought_level',currentValue:'medium',options:[{value:'medium',name:'Medium'}]}]});assert.equal(catalog.currentModelId,'catalog-model');assert.deepEqual(catalog.efforts,[{id:'medium',name:'Medium'}]);
});

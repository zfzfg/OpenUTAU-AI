import { randomUUID } from 'node:crypto';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { ChildProcessWithoutNullStreams } from 'node:child_process';
import { nativeGrant } from '../mcp-server/discovery.js';
import { AutomationError, requireString } from '../contracts/errors.js';
import { RpcPeer } from './json-rpc.js';
import { provider, providerLaunch, probeProvider, PROVIDERS, sessionCatalog, providerError, type ProviderBackend } from './providers.js';
import { startProcess, stopOwnedProcess } from './process.js';
import { QuestionService } from './question-service.js';
import { MUSICAL_INSTRUCTIONS_V1 } from './instructions.js';

interface PendingCard {requestId:string;turnId?:string;processGeneration:number;kind:'permission'|'question';options:any[];resolve:(value:unknown)=>void;reject:(error:Error)=>void;}
interface HostSession {
  sessionId:string;nativeSessionId:string;processGeneration:number;backend:ProviderBackend;child:ChildProcessWithoutNullStreams;peer:RpcPeer;
  clientId:string;instanceId:string;projectSessionId:string;accessGrantRef:string;helperRef?:string;cwd:string;mode:string;
  state:string;turnId?:string;turn?:Promise<unknown>;text:string;catalog:ReturnType<typeof sessionCatalog>;agentCapabilities:Record<string,any>;
  cards:Map<string,PendingCard>;tools:Map<string,Record<string,any>>;disposed:boolean;closing?:Promise<void>;
}
export function injectMcp(existing:any[],configuration:Record<string,unknown>,helper?:Record<string,unknown>):any[]{
  const result:any[]=[];
  for(const entry of existing||[]){
    if(!entry||typeof entry.name!=='string')throw new AutomationError('INVALID_ARGUMENT','Invalid MCP configuration entry.');
    if(entry.name==='openutau'||entry.name==='openutau-assistant'){
      const desired=entry.name==='openutau'?configuration:helper;
      if(!desired||JSON.stringify(entry)!==JSON.stringify(desired))throw new AutomationError('MCP_CONFIG_COLLISION','An existing MCP entry uses an OpenUtau reserved name with a different configuration.');
      continue;
    }
    if(result.some(x=>x.name===entry.name))throw new AutomationError('MCP_CONFIG_COLLISION','Duplicate MCP server names are not accepted.');result.push(entry);
  }
  result.push(configuration);if(helper)result.push(helper);return result;
}
function toolResult(update:any):unknown {
  if(update.rawOutput!==undefined)return update.rawOutput;
  for(const block of update.content||[]){const content=block.content||block;if(content.type==='text'&&typeof content.text==='string'){try{const value=JSON.parse(content.text);if(value&&typeof value==='object'&&'ok' in value)return value;}catch{}}}
  return undefined;
}
export class AgentHost {
  private initialized=false;private processGeneration=1;private sessions=new Map<string,HostSession>();private questions:QuestionService;private stopped=false;
  private entryDirectory:string;
  constructor(readonly notify:(method:string,params:Record<string,unknown>)=>void,entryDirectory=dirname(fileURLToPath(import.meta.url))){
    this.entryDirectory=entryDirectory;this.questions=new QuestionService((method,params)=>{
      const session=this.sessions.get(String(params.sessionId));
      if(session&&method==='questionRequested')this.state(session,'waiting_user');
      if(session&&method==='requestWithdrawn'&&params.reason==='answered'&&!session.cards.size&&!this.questions.hasPending(session.sessionId))this.state(session,'working');
      notify(method,params);
    });
  }
  private emit(session:HostSession,method:string,payload:Record<string,unknown>={}){this.notify(method,{sessionId:session.sessionId,...(session.turnId?{turnId:session.turnId}:{}),processGeneration:session.processGeneration,eventId:randomUUID(),...payload});}
  private state(session:HostSession,state:string){session.state=state;this.emit(session,'sessionState',{state});}
  private session(id:unknown):HostSession{const found=this.sessions.get(requireString(id,'sessionId'));if(!found||found.disposed)throw new AutomationError('SESSION_NOT_FOUND','The native assistant session is not active.');return found;}
  async dispatch(method:string,params:Record<string,any>):Promise<unknown>{
    if(method==='initialize'){
      if(this.initialized)throw new AutomationError('PROTOCOL_MISMATCH','The host handshake can run only once.');
      if(params.apiVersion!=='1')throw new AutomationError('PROTOCOL_MISMATCH','The assistant host requires contract version 1.');
      if(params.processGeneration!==undefined&&(!Number.isSafeInteger(params.processGeneration)||params.processGeneration<1))throw new AutomationError('INVALID_ARGUMENT','processGeneration must be a positive integer.');
      this.processGeneration=params.processGeneration||1;this.initialized=true;
      return {apiVersion:'1',hostVersion:'1.0.0',processGeneration:this.processGeneration,capabilities:{acpVersion:1,questions:true,permissions:true,models:'dynamic',efforts:'dynamic',streaming:true,cancel:true,nativeToolIsolation:'unknown'},runtime:{nodeVersion:process.version,platform:process.platform,arch:process.arch}};
    }
    if(!this.initialized||this.stopped)throw new AutomationError('PROTOCOL_MISMATCH','Initialize the assistant host before using it.');
    switch(method){
      case 'providers.list':return {providers:PROVIDERS.map(x=>({id:x.id,name:x.name,status:'unprobed',capabilities:{musicTools:x.musicTools,questionRpc:x.questionRpc,nativeFileAndShellIsolation:'unknown'}}))};
      case 'providers.probe':{const info=await probeProvider(params);this.notify('providerState',{eventId:randomUUID(),processGeneration:this.processGeneration,...info});return info;}
      case 'session.create':case 'session.load':return this.create(params,method==='session.load');
      case 'session.prompt':return this.prompt(this.session(params.sessionId),params);
      case 'session.cancel':return this.cancel(this.session(params.sessionId),params.turnId);
      case 'session.setModel':case 'session.setEffort':return this.configure(this.session(params.sessionId),method,params);
      case 'session.setMode':return this.mode(this.session(params.sessionId),params);
      case 'session.steer':throw new AutomationError('UNSUPPORTED_CAPABILITY','Steering has not been verified for this provider adapter. Cancel the turn and send a new message.');
      case 'permission.respond':case 'question.respond':return this.respond(method,params);
      case 'session.list':return this.list(params);
      case 'session.dispose':{await this.disposeSession(this.session(params.sessionId));return {disposed:true};}
      case 'shutdown':await this.dispose();return {shutdown:true};
      default:throw new AutomationError('METHOD_NOT_FOUND','Unknown assistant host method.');
    }
  }
  private async create(params:Record<string,any>,load:boolean){
    const backend=provider(params.providerId);const cwd=requireString(params.cwd,'cwd');const accessGrantRef=requireString(params.accessGrantRef,'accessGrantRef');
    const instanceId=requireString(params.instanceId,'instanceId');const projectSessionId=requireString(params.projectSessionId,'projectSessionId');
    const grant=await nativeGrant(accessGrantRef,{OPENUTAU_INSTANCE_ID:instanceId});
    if(grant.projectSessionId!==projectSessionId)throw new AutomationError('PROJECT_CHANGED','The grant refers to another project session.');
    if(grant.processGeneration!==undefined&&grant.processGeneration!==this.processGeneration)throw new AutomationError('UNAUTHORIZED','The grant belongs to an older host generation.');
    const launch=await providerLaunch(backend,params);
    const child=await startProcess(launch.spec,cwd);const peer=new RpcPeer(child.stdout,child.stdin);
    const session:HostSession={sessionId:randomUUID(),nativeSessionId:load?requireString(params.nativeSessionId,'nativeSessionId'):'',processGeneration:this.processGeneration,backend,child,peer,clientId:params.clientId||grant.clientId||`assistant_${randomUUID()}`,instanceId,projectSessionId,accessGrantRef,cwd,mode:grant.mode||params.mode||'preview',state:'starting',text:'',catalog:sessionCatalog({}),agentCapabilities:{},cards:new Map(),tools:new Map(),disposed:false};
    this.sessions.set(session.sessionId,session);
    // Providers may emit replay updates before session/load finishes.
    peer.onNotification=(method,value)=>{if(method==='session/update'&&(!session.nativeSessionId||value.sessionId===session.nativeSessionId)&&(session.turn||session.state==='starting'))this.update(session,value.update||{});};
    peer.onRequest=(method,value)=>this.agentRequest(session,method,value);
    peer.onEnd=error=>{if(!session.disposed){this.withdraw(session,'process_ended');this.state(session,'failed');this.emit(session,'error',providerError(error));void stopOwnedProcess(child).catch(()=>{});}};
    child.stderr.on('data',()=>{}); // Provider diagnostics can contain tokens/transcripts; consume without recording them.
    child.once('close',(code,signal)=>{this.withdraw(session,'process_exited');if(!session.disposed)this.state(session,'failed');this.emit(session,'processExited',{code,signal});});
    try{
      this.state(session,'starting');
      const init=await peer.request('initialize',{protocolVersion:1,clientInfo:{name:'OpenUtau',version:'1.0.0'},clientCapabilities:{fs:{readTextFile:false,writeTextFile:false},terminal:false}});
      if(init.protocolVersion!==1)throw new AutomationError('PROTOCOL_MISMATCH','This host implements ACP v1; the provider negotiated another version.');
      session.agentCapabilities=init.agentCapabilities||{};
      if(load&&!session.agentCapabilities.loadSession)throw new AutomationError('UNSUPPORTED_CAPABILITY','This provider does not advertise session/load.');
      session.helperRef=await this.questions.bindSession(session.sessionId,session.processGeneration,accessGrantRef,()=>({sessionId:session.sessionId,turnId:session.turnId,processGeneration:session.processGeneration}));
      const env=[{name:'OPENUTAU_INSTANCE_ID',value:instanceId},{name:'OPENUTAU_CLIENT_ID',value:session.clientId},{name:'OPENUTAU_ACCESS_GRANT_REF',value:accessGrantRef}];
      const mcp={name:'openutau',command:process.execPath,args:[join(this.entryDirectory,'mcp-server.js'),'--stdio'],env};
      const helper={name:'openutau-assistant',command:process.execPath,args:[join(this.entryDirectory,'assistant-tools.js'),'--stdio'],env:[{name:'OPENUTAU_ASSISTANT_HELPER_REF',value:session.helperRef}]};
      const mcpServers=injectMcp(params.mcpServers||[],mcp,helper);
      const data=await peer.request(load?'session/load':'session/new',{cwd,mcpServers,...(load?{sessionId:session.nativeSessionId}:{})},30000);
      if(!load)session.nativeSessionId=requireString(data.sessionId,'provider sessionId');
      session.catalog=sessionCatalog(data);
      if(params.modelId)await this.configure(session,'session.setModel',params);
      if(params.effortId)await this.configure(session,'session.setEffort',params);
      this.state(session,'ready');this.emit(session,'models',this.publicCatalog(session));this.emit(session,'capabilities',{capabilities:this.capabilities(session)});
      return {sessionId:session.sessionId,nativeSessionId:session.nativeSessionId,processGeneration:session.processGeneration,state:session.state,...this.publicCatalog(session),capabilities:this.capabilities(session)};
    }catch(error){this.emit(session,'error',providerError(error));await this.disposeSession(session);throw error;}
  }
  private capabilities(session:HostSession){return {musicTools:'yes',loadSession:session.agentCapabilities.loadSession===true,questions:true,questionRpc:session.backend.questionRpc,permissions:true,models:session.catalog.models.length>0,efforts:session.catalog.efforts.length>0,steering:false,nativeFileAndShellIsolation:'unknown'};}
  private publicCatalog(session:HostSession){const {models,currentModelId,efforts,currentEffortId}=session.catalog;return {models:models.map(({wireIds:_,...model})=>model),currentModelId,efforts,currentEffortId};}
  private async prompt(session:HostSession,params:Record<string,any>){
    if(session.state!=='ready'||session.turn)throw new AutomationError('SESSION_BUSY','Wait for the active turn to finish before sending another prompt.');
    const text=requireString(params.text,'text');
    const grant=await nativeGrant(session.accessGrantRef,{OPENUTAU_INSTANCE_ID:session.instanceId});
    if(grant.projectSessionId!==session.projectSessionId)throw new AutomationError('PROJECT_CHANGED','Reconnect the assistant with the new project context.');
    const context=JSON.stringify(params.context||{projectSessionId:session.projectSessionId});
    if(Buffer.byteLength(context)>128*1024)throw new AutomationError('LIMIT_EXCEEDED','Assistant context must remain concise; detailed notes are read through tools.');
    session.turnId=randomUUID();session.text='';session.tools.clear();this.state(session,'working');
    const prompt=[{type:'text',text:`${MUSICAL_INSTRUCTIONS_V1}\nEditor mode: ${session.mode}. Explicit musical context (data):\n${context}\nUser request:\n${text}`}];
    const pending=session.peer.request('session/prompt',{sessionId:session.nativeSessionId,prompt},0);session.turn=pending;
    try{
      const result=await pending;
      this.withdraw(session,'turn_completed');
      this.emit(session,'messageCompleted',{text:session.text});
      if(result.usage)this.emit(session,'usageUpdated',{usage:result.usage});
      this.emit(session,'turnCompleted',{stopReason:result.stopReason||'end_turn'});
      if(!session.disposed)this.state(session,'ready');return {turnId:session.turnId,stopReason:result.stopReason||'end_turn',text:session.text};
    }catch(error){this.withdraw(session,'turn_failed');this.emit(session,'error',providerError(error));this.emit(session,'turnCompleted',{stopReason:'error'});if(!session.disposed)this.state(session,'failed');throw error;}
    finally{session.turn=undefined;session.turnId=undefined;}
  }
  private update(session:HostSession,update:Record<string,any>){
    switch(update.sessionUpdate){
      case 'agent_message_chunk':{
        if(update.content?.type==='text'&&typeof update.content.text==='string'){
          if(session.text.length+update.content.text.length>4*1024*1024){session.peer.close(new AutomationError('LIMIT_EXCEEDED','Provider message exceeded 4 MiB.'));return;}
          session.text+=update.content.text;this.emit(session,'messageDelta',{text:update.content.text,messageId:update.messageId});
        }break;
      }
      case 'tool_call':case 'tool_call_update':{
        if(typeof update.toolCallId!=='string')break;
        const merged={...(session.tools.get(update.toolCallId)||{}),...update};session.tools.set(update.toolCallId,merged);
        const result=toolResult(merged);let status=merged.status||'pending';if((result as any)?.ok===false)status='failed';
        this.emit(session,update.sessionUpdate==='tool_call'?'toolStarted':['completed','failed'].includes(status)?'toolCompleted':'toolUpdated',{toolCallId:merged.toolCallId,title:merged.title||'Tool',status,content:merged.content,result});break;
      }
      case 'plan':this.emit(session,'planUpdated',{entries:update.entries||[]});break;
      case 'usage_update':this.emit(session,'usageUpdated',{usage:update});break;
      case 'config_option_update':session.catalog=sessionCatalog({...update,models:{availableModels:session.catalog.models,currentModelId:session.catalog.currentModelId}});this.emit(session,'models',this.publicCatalog(session));break;
      case 'current_mode_update':this.emit(session,'sessionState',{state:session.state,nativeModeId:update.currentModeId});break;
    }
  }
  private agentRequest(session:HostSession,method:string,params:Record<string,any>):Promise<unknown>{
    if(params.sessionId!==session.nativeSessionId)return Promise.reject(new AutomationError('REQUEST_STALE','ACP request belongs to another session.'));
    if(session.backend.id==='grok'&&['x.ai/ask_user_question','_x.ai/ask_user_question'].includes(method)){
      if(!Array.isArray(params.questions)||params.questions.length<1||params.questions.length>4)return Promise.reject(new AutomationError('PROTOCOL_MISMATCH','The Grok interview must contain one to four questions.'));
      return Promise.all(params.questions.map((question:any)=>{
        if(typeof question.question!=='string')throw new AutomationError('PROTOCOL_MISMATCH','Grok question text is missing.');
        const options=(question.options||[]).map((option:any,index:number)=>({optionId:String(index),name:option.label||option.name}));
        return this.agentRequest(session,'session/request_question',{sessionId:params.sessionId,title:question.header,question:question.question,options}).then((response:any)=>({question,options,response}));
      })).then(answers=>{
        if(answers.some(x=>x.response.cancelled))return {outcome:'cancelled'};
        return {outcome:'accepted',answers:Object.fromEntries(answers.map(({question,options,response})=>[question.question,response.answer||options.find((x:any)=>x.optionId===response.optionId)?.name||''])),annotations:{}};
      });
    }
    const kind=method==='session/request_permission'?'permission':['session/request_question','session/ask_question','_x.ai/ask_question'].includes(method)&&session.backend.questionRpc?'question':undefined;
    if(!kind)return Promise.reject(new AutomationError('UNSUPPORTED_CAPABILITY','The native sidebar does not expose a filesystem or terminal RPC.'));
    if(!session.turnId)return Promise.reject(new AutomationError('REQUEST_STALE','Interactive requests require an active turn.'));
    const options=Array.isArray(params.options)?params.options:[];
    if(kind==='permission'&&options.some((x:any)=>typeof x.optionId!=='string'||typeof x.name!=='string'))return Promise.reject(new AutomationError('PROTOCOL_MISMATCH','Provider permission options are malformed.'));
    if(session.cards.size>=8)return Promise.reject(new AutomationError('LIMIT_EXCEEDED','This turn already has eight pending cards.'));
    return new Promise((resolve,reject)=>{
      const requestId=randomUUID();session.cards.set(requestId,{requestId,turnId:session.turnId,processGeneration:session.processGeneration,kind,options,resolve,reject});
      this.state(session,'waiting_user');
      this.emit(session,kind==='permission'?'permissionRequested':'questionRequested',{requestId,title:params.toolCall?.title||params.title||'Question',question:params.question||params.prompt,options,toolCall:params.toolCall});
    });
  }
  private respond(method:string,params:Record<string,any>){
    const session=this.session(params.sessionId);const card=session.cards.get(params.requestId);
    if(!card&&method==='question.respond')return this.questions.respond(params);
    if(!card||card.kind!==(method==='permission.respond'?'permission':'question')||card.turnId!==params.turnId||card.processGeneration!==params.processGeneration)throw new AutomationError('REQUEST_STALE','The request is no longer pending in this session, turn and generation.');
    if(card.kind==='permission'&&!card.options.some(x=>x.optionId===params.optionId))throw new AutomationError('INVALID_ARGUMENT','Select an unchanged provider optionId.');
    if(card.kind==='question'&&card.options.length&&!card.options.some(x=>x.optionId===params.optionId))throw new AutomationError('INVALID_ARGUMENT','Select an offered question optionId.');
    session.cards.delete(card.requestId);
    card.resolve(card.kind==='permission'?{outcome:{outcome:'selected',optionId:params.optionId}}:{answer:params.answer,...(params.optionId?{optionId:params.optionId}:{})});
    this.emit(session,'requestWithdrawn',{requestId:card.requestId,reason:'answered'});if(!session.cards.size)this.state(session,'working');return {accepted:true};
  }
  private withdraw(session:HostSession,reason:string){
    this.questions.withdraw(session.sessionId,reason);
    for(const card of session.cards.values()){card.resolve(card.kind==='permission'?{outcome:{outcome:'cancelled'}}:{cancelled:true});this.emit(session,'requestWithdrawn',{requestId:card.requestId,reason});}session.cards.clear();
  }
  private async cancel(session:HostSession,turnId?:string){
    if(turnId&&turnId!==session.turnId)throw new AutomationError('REQUEST_STALE','The selected turn has already ended.');
    if(!session.turn)return {cancelled:true};
    this.state(session,'cancelling');this.withdraw(session,'cancelled');
    session.peer.notify('session/cancel',{sessionId:session.nativeSessionId});
    const pending=session.turn;
    let timer:NodeJS.Timeout|undefined;
    const ended=await Promise.race([pending.then(()=>true,()=>true),new Promise<false>(resolve=>{timer=setTimeout(()=>resolve(false),3000);})]);if(timer)clearTimeout(timer);
    if(!ended){await stopOwnedProcess(session.child);session.peer.close(new AutomationError('CANCELLED','Provider did not confirm cancellation and was terminated.'));this.state(session,'failed');}
    return {cancelled:true,processTerminated:!ended};
  }
  private async configure(session:HostSession,method:string,params:Record<string,any>){
    if(session.turn)throw new AutomationError('SESSION_BUSY','Model and effort changes require an idle turn.');
    const modelId=method==='session.setModel'?requireString(params.modelId,'modelId'):session.catalog.currentModelId;
    const model=session.catalog.models.find(x=>x.modelId===modelId);if(!model)throw new AutomationError('INVALID_ARGUMENT','The selected model is not in the verified session catalog.');
    const effortId=method==='session.setEffort'?requireString(params.effortId,'effortId'):params.effortId||session.catalog.currentEffortId;
    if(effortId&&!model.efforts?.some(x=>x.id===effortId))throw new AutomationError('INVALID_ARGUMENT','The selected effort is not offered for this model.');
    let data:Record<string,any>;
    if(method==='session.setEffort'&&session.catalog.effortConfigId)data=await session.peer.request('session/set_config_option',{sessionId:session.nativeSessionId,configId:session.catalog.effortConfigId,value:effortId});
    else if(method==='session.setModel'&&session.catalog.modelConfigId)data=await session.peer.request('session/set_config_option',{sessionId:session.nativeSessionId,configId:session.catalog.modelConfigId,value:modelId});
    else{const wireId=effortId?model.wireIds?.[effortId]:undefined;if(method==='session.setEffort'&&!wireId)throw new AutomationError('UNSUPPORTED_CAPABILITY','This adapter does not advertise a confirmed effort control.');data=await session.peer.request('session/set_model',{sessionId:session.nativeSessionId,modelId:wireId||modelId});}
    if(data.configOptions||data.models)session.catalog=sessionCatalog({...data,...(!data.models?{models:{availableModels:session.catalog.models,currentModelId:modelId}}:{})});
    else{session.catalog.currentModelId=modelId;if(effortId)session.catalog.currentEffortId=effortId;session.catalog.efforts=model.efforts||[];}
    this.emit(session,'models',this.publicCatalog(session));return this.publicCatalog(session);
  }
  private async mode(session:HostSession,params:Record<string,any>){
    if(!['read_only','preview','apply'].includes(params.mode))throw new AutomationError('INVALID_ARGUMENT','Choose read_only, preview or apply.');
    if(session.turn)throw new AutomationError('SESSION_BUSY','Cancel or finish the turn before changing editor mode.');
    if(params.accessGrantRef&&params.accessGrantRef!==session.accessGrantRef)throw new AutomationError('RECONNECT_REQUIRED','Reconnect or load the native session to inject a different grant reference.');
    const grant=await nativeGrant(params.accessGrantRef||session.accessGrantRef,{OPENUTAU_INSTANCE_ID:session.instanceId});
    if(grant.projectSessionId!==session.projectSessionId||grant.processGeneration!==undefined&&grant.processGeneration!==session.processGeneration||grant.mode!==params.mode)throw new AutomationError('UNAUTHORIZED','Only the app can change the musical access grant.');
    session.accessGrantRef=params.accessGrantRef||session.accessGrantRef;session.mode=params.mode;return {mode:session.mode,nativeFileAndShellIsolation:'unknown'};
  }
  private async list(params:Record<string,any>){
    if(params.sessionId){const session=this.session(params.sessionId);if(session.agentCapabilities.sessionCapabilities?.list)return session.peer.request('session/list',{cwd:session.cwd,...(params.cursor?{cursor:params.cursor}:{})});}
    return {sessions:[...this.sessions.values()].filter(x=>!x.disposed).map(x=>({sessionId:x.sessionId,nativeSessionId:x.nativeSessionId,providerId:x.backend.id,cwd:x.cwd,state:x.state})),nativeHistoryAvailable:false};
  }
  private async disposeSession(session:HostSession){
    if(session.closing)return session.closing;
    session.closing=(async()=>{this.withdraw(session,'disposed');this.questions.unbind(session.sessionId);session.disposed=true;try{if(session.turn){session.peer.notify('session/cancel',{sessionId:session.nativeSessionId});}}catch{}await stopOwnedProcess(session.child);session.peer.close();this.emit(session,'sessionState',{state:'disconnected'});this.sessions.delete(session.sessionId);})();
    return session.closing;
  }
  async dispose(){if(this.stopped)return;this.stopped=true;const outcomes=await Promise.allSettled([...this.sessions.values()].map(x=>this.disposeSession(x)));await this.questions.dispose();const failed=outcomes.find(x=>x.status==='rejected');if(failed?.status==='rejected')throw failed.reason;}
}

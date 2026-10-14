import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';

const root = fileURLToPath(new URL('../../../', import.meta.url));
const out = resolve(root, 'contracts/automation/v1');
const str = {type:'string', minLength:1, maxLength:4096};
const integer = {type:'integer', minimum:0, maximum:2147483647};
const number = {type:'number'};
const id = {...str, maxLength:256};
const arr = (items, maxItems=1000) => ({type:'array',items,maxItems});
const obj = (properties, required=[], more=false) => ({type:'object', properties, required, additionalProperties:more});
const field = (e) => ({type:'string',enum:e});
const ref = (name) => ({$ref:`#/$defs/${name}`});
const schema = (name, body) => ({$schema:'https://json-schema.org/draft/2020-12/schema',$id:`https://openutau.org/automation/v1/${name}.schema.json`,...body});
const entity = {trackId:id,partId:id,noteId:id,clientRef:id};
const note = obj({clientRef:id,positionTicks:integer,durationTicks:{...integer,minimum:1},tone:{type:'integer',minimum:0,maximum:127},lyric:{type:'string',maxLength:4096},tuningCents:number},['positionTicks','durationTicks','tone']);
const noteUpdate = obj({noteId:id,positionTicks:integer,durationTicks:{...integer,minimum:1},tone:{type:'integer',minimum:0,maximum:127},lyric:{type:'string',maxLength:4096},tuningCents:number},['noteId']);
const op = (type,properties,required=[]) => obj({type:{const:type},...properties},['type',...required]);
const tempo = obj({positionTicks:integer,bpm:{type:'number',exclusiveMinimum:0,maximum:1000}},['positionTicks','bpm']);
const timeSignature = obj({barPosition:integer,numerator:{type:'integer',minimum:1,maximum:32},denominator:{type:'integer',enum:[1,2,4,8,16,32]}},['barPosition','numerator','denominator']);
const point = obj({xMs:number,yCents:number,shape:field(['io','i','o','l'])},['xMs','yCents']);
const operations = [
  op('set_project_info',{name:{type:'string',maxLength:4096},comment:{type:'string',maxLength:65536},key:{type:'integer',minimum:0,maximum:11}}),
  op('set_tempo_map',{tempos:arr(tempo)},['tempos']),
  op('set_time_signature_map',{timeSignatures:arr(timeSignature)},['timeSignatures']),
  op('add_track',{clientRef:id,name:{type:'string',maxLength:4096},singerId:id,phonemizerId:id,volumeDb:number,pan:{type:'number',minimum:-100,maximum:100},mute:{type:'boolean'},solo:{type:'boolean'},color:{type:'string'}}),
  op('update_track',{trackId:id,name:{type:'string',maxLength:4096},singerId:id,phonemizerId:id,volumeDb:number,pan:{type:'number',minimum:-100,maximum:100},mute:{type:'boolean'},solo:{type:'boolean'},color:{type:'string'}},['trackId']),
  op('remove_track',{trackId:id},['trackId']),
  op('add_part',{clientRef:id,trackId:id,name:{type:'string',maxLength:4096},positionTicks:integer,durationTicks:{...integer,minimum:1},kind:field(['voice'])},['trackId','positionTicks','durationTicks']),
  op('update_part',{partId:id,trackId:id,name:{type:'string',maxLength:4096},positionTicks:integer,durationTicks:{...integer,minimum:1}},['partId']),
  op('remove_part',{partId:id},['partId']),
  op('add_notes',{partId:id,notes:arr(note)},['partId','notes']),
  op('update_notes',{partId:id,items:arr(noteUpdate)},['partId','items']),
  op('remove_notes',{partId:id,noteIds:arr(id)},['partId','noteIds']),
  op('set_lyrics',{partId:id,items:arr(obj({noteId:id,lyric:{type:'string',maxLength:4096}},['noteId','lyric'])),text:{type:'string',maxLength:65536},noteIds:arr(id)},['partId']),
  op('set_phonetic_hints',{partId:id,items:arr(obj({noteId:id,lyric:{type:'string',maxLength:4096},phoneticHint:{type:'string',maxLength:4096},alphabet:field(['phonemizer'])},['noteId','phoneticHint']))},['partId','items']),
  op('set_phoneme_overrides',{partId:id,phonemeGeneration:integer,items:arr(obj({noteId:id,phonemeIndex:integer,alias:{type:'string',maxLength:4096},offsetTicks:{type:'integer'},preutterDeltaMs:number,overlapDeltaMs:number,attackTimeDeltaMs:number,releaseTimeDeltaMs:number},['noteId','phonemeIndex']))},['partId','phonemeGeneration','items']),
  op('clear_phoneme_overrides',{partId:id,phonemeGeneration:integer,noteIds:arr(id)},['partId','phonemeGeneration','noteIds']),
  op('set_pitch',{partId:id,items:arr(obj({noteId:id,points:arr(point),snapFirst:{type:'boolean'}},['noteId','points']))},['partId','items']),
  op('set_vibrato',{partId:id,items:arr(obj({noteId:id,lengthPercent:number,periodMs:number,depthCents:number,fadeInPercent:number,fadeOutPercent:number,shiftPercent:number,driftPercent:number,volumeLinkPercent:number},['noteId']))},['partId','items']),
  op('set_expressions',{partId:id,items:arr(obj({noteId:id,abbr:id,phonemeIndex:integer,value:number,points:arr(obj({positionTicks:integer,value:number},['positionTicks','value']))},['abbr']))},['partId','items'])
];
const edit = schema('edit',obj({instanceId:id,projectSessionId:id,expectedEditRevision:integer,operationId:id,runId:id,label:{type:'string',maxLength:1024},operations:{...arr({oneOf:operations}),minItems:1}},['projectSessionId','expectedEditRevision','operationId','operations']));
const errors = ['APP_NOT_RUNNING','APP_UNAVAILABLE','AUTOMATION_DISABLED','APP_INITIALIZING','INSTANCE_AMBIGUOUS','UNAUTHORIZED','PROTOCOL_MISMATCH','PROJECT_CHANGED','SNAPSHOT_EXPIRED','ENTITY_NOT_FOUND','REVISION_CONFLICT','PHONEME_GENERATION_CONFLICT','EDITOR_BUSY','INVALID_ARGUMENT','LIMIT_EXCEEDED','UNSUPPORTED_CAPABILITY','MISSING_SINGER','MISSING_ALIAS','PHONEMIZER_FAILED','RENDER_FAILED','FILE_ACCESS_DENIED','IDEMPOTENCY_CONFLICT','CANCELLED','OPERATION_STATE_UNKNOWN'];
const result = schema('results',obj({ok:{type:'boolean'},error:obj({code:field(errors),message:str,details:obj({},[],true),hint:{type:'string'}},['code','message']),projectSessionId:id,editRevision:integer,operationId:id,status:str},['ok'],true));
const common = {instanceId:id};
const write = {...common,projectSessionId:id,expectedEditRevision:integer,operationId:id};
const writeRequired = ['projectSessionId','expectedEditRevision','operationId'];
const page = {cursor:id,limit:{type:'integer',minimum:1,maximum:1000}};
const definitions = [];
function tool(name,description,properties={},required=[],read=true,destructive=false,idempotent=true){definitions.push({name:`openutau_${name}`,description,inputSchema:obj({...common,...properties},required),outputSchema:result,annotations:{title:description,readOnlyHint:read,destructiveHint:destructive,idempotentHint:idempotent,openWorldHint:false}});}
tool('list_instances','List reachable OpenUtau processes; never choose an ambiguous instance implicitly.');
tool('get_project','Read project session, edit revision, tick resolution, tempo, tracks and parts. Notes are paginated.');
tool('get_selection','Read current track, part and note selection, including detached piano roll.');
tool('get_capabilities','Read operation, renderer, expression and access limits before planning edits.');
tool('list_singers','List installed usable singers without disclosing their private filesystem paths.',{query:{type:'string',maxLength:1024},...page});
tool('list_phonemizers','List available phonemizers; unknown language or phonetic capabilities remain unknown.',page);
tool('get_notes','Read notes from a stable snapshot; note positions are relative to the part.',{partId:id,fromTicks:integer,toTicks:integer,include:arr(field(['pitch','vibrato','expressions','overrides'])),...page},['partId']);
tool('get_phonemes','Read aliases, timing and exact generation; stale phonemes are not a valid edit basis.',{partId:id,noteIds:arr(id),...page},['partId']);
tool('get_diagnostics','Read missing aliases, durations, singer and derived processing errors.',{partId:id,...page});
tool('preview_edit','Validate an edit and return a musical diff without changing revision or undo.',edit.properties,edit.required);
tool('apply_edit_batch','Commit one undoable transaction guarded by project, revision and operation ID. Preview grants return a proposal.',edit.properties,edit.required,false,true,true);
tool('register_input_file','Register an existing absolute path inside a released workspace; receive a session-bound fileHandle.',{path:str,purpose:field(['project','midi','audio'])},['path','purpose'],false,false,false);
tool('resolve_output_target','Validate an absolute output path inside a released root; overwrite must be explicit.',{path:str,purpose:field(['project','audio']),overwrite:{type:'boolean',default:false}},['path','purpose'],false,false,false);
tool('new_project','Create a new project using native Save/Cancel lifecycle; invalidate previous project identities.',{...write,name:{type:'string',maxLength:4096},discardChanges:{type:'boolean'}},writeRequired,false,true,true);
tool('open_project','Open a registered project file using native Save/Cancel lifecycle.',{...write,fileHandle:id,discardChanges:{type:'boolean'}},[...writeRequired,'fileHandle'],false,true,true);
tool('import_midi','Import a registered MIDI file through undoable editor commands.',{...write,fileHandle:id,preview:{type:'boolean'},importTempo:{type:'boolean'},trackId:id,positionTicks:integer},[...writeRequired,'fileHandle'],false,false,true);
tool('import_audio','Import a registered audio file as an undoable wave part.',{...write,fileHandle:id,trackId:id,positionTicks:integer,preview:{type:'boolean'}},[...writeRequired,'fileHandle'],false,false,true);
tool('wait_for_phonemes','Wait boundedly for the exact edit generation; pending and stale are explicit.',{projectSessionId:id,expectedEditRevision:integer,partId:id,timeoutMs:{type:'integer',minimum:0,maximum:30000}},['projectSessionId','expectedEditRevision','partId']);
tool('start_render','Start a preview, stems or mixdown job at an explicit project revision.',{...write,kind:field(['preview','stems','mixdown']),partId:id,trackIds:arr(id),fromTicks:integer,toTicks:integer},[...writeRequired,'kind'],false,false,true);
tool('get_job','Read job progress, typed failures and verified output artifacts.',{jobId:id},['jobId']);
tool('cancel_job','Request job cancellation; cancellation is confirmed separately from the request.',{jobId:id},['jobId'],false,false,true);
tool('get_operation','Resolve an operation after timeout; never blindly repeat a possibly committed edit.',{operationId:id},['operationId']);
tool('revert_operation','Revert a compatible most recent owned transaction without undoing intervening user work.',{...write,targetOperationId:id},[...writeRequired,'targetOperationId'],false,true,true);
tool('play_range','Play an explicit project or preview range through the shared audio coordinator.',{projectSessionId:id,fromTicks:integer,toTicks:integer,artifactId:id},['projectSessionId'],false,false,false);
tool('stop_playback','Stop project and preview audio.',{},[],false,false,true);
tool('save_project','Save the guarded project to a validated outputHandle.',{...write,outputHandle:id,overwrite:{type:'boolean',default:false}},[...writeRequired,'outputHandle'],false,true,true);
tool('export_audio','Export preview, stems or mixdown as a job to a validated outputHandle.',{...write,kind:field(['preview','stems','mixdown']),outputHandle:id,overwrite:{type:'boolean',default:false},partId:id,trackIds:arr(id),fromTicks:integer,toTicks:integer},[...writeRequired,'kind','outputHandle'],false,true,true);
const methods=['initialize','providers.list','providers.probe','session.create','session.load','session.list','session.prompt','session.cancel','session.setModel','session.setEffort','session.setMode','session.steer','permission.respond','question.respond','session.dispose','shutdown'];
const notifications=['providerState','sessionState','capabilities','models','messageDelta','messageCompleted','toolStarted','toolUpdated','toolCompleted','permissionRequested','questionRequested','requestWithdrawn','planUpdated','usageUpdated','error','turnCompleted','processExited'];
const sessionParams={providerId:field(['codex','claude','grok','gemini','muse']),cwd:str,cliPath:str,adapterPath:str,instanceId:id,projectSessionId:id,accessGrantRef:str,clientId:id,modelId:id,effortId:id,mode:field(['read_only','preview','apply']),mcpServers:arr(obj({name:id,command:str,args:arr({type:'string'}),env:arr(obj({name:id,value:{type:'string'}},['name','value']))},['name','command','args','env']),32)};
const contextFields={sessionId:id,turnId:id,processGeneration:{type:'integer',minimum:1}};
const requestSchemas={
  initialize:obj({apiVersion:{const:'1'},appVersion:str,processGeneration:{type:'integer',minimum:1}},['apiVersion']),
  'providers.list':obj({}),
  'providers.probe':obj({providerId:sessionParams.providerId,cliPath:str,adapterPath:str},['providerId']),
  'session.create':obj(sessionParams,['providerId','cwd','instanceId','projectSessionId','accessGrantRef']),
  'session.load':obj({...sessionParams,nativeSessionId:id},['providerId','cwd','instanceId','projectSessionId','accessGrantRef','nativeSessionId']),
  'session.list':obj({sessionId:id,cursor:id}),
  'session.prompt':obj({sessionId:id,text:{type:'string',minLength:1,maxLength:4096},context:obj({},[],true)},['sessionId','text']),
  'session.cancel':obj({sessionId:id,turnId:id},['sessionId']),
  'session.setModel':obj({sessionId:id,modelId:id,effortId:id},['sessionId','modelId']),
  'session.setEffort':obj({sessionId:id,effortId:id},['sessionId','effortId']),
  'session.setMode':obj({sessionId:id,mode:sessionParams.mode,accessGrantRef:str},['sessionId','mode']),
  'session.steer':obj({sessionId:id,text:str},['sessionId','text']),
  'permission.respond':obj({...contextFields,requestId:id,optionId:id},['sessionId','turnId','processGeneration','requestId','optionId']),
  'question.respond':obj({...contextFields,requestId:id,optionId:id,answer:{type:'string',maxLength:16384}},['sessionId','turnId','processGeneration','requestId']),
  'session.dispose':obj({sessionId:id},['sessionId']),shutdown:obj({})
};
const option = obj({optionId:id,name:str,kind:str},['optionId','name']);
const eventFields={...contextFields,eventId:id};
const eventSchemas={
  providerState:obj({eventId:id,processGeneration:integer,providerId:sessionParams.providerId,status:str,cliVersion:str,adapterVersion:str,message:str,capabilities:obj({},[],true)},['providerId','status'],true),
  sessionState:obj({...eventFields,state:field(['disconnected','starting','ready','working','waiting_user','cancelling','failed']),nativeModeId:id},['sessionId','processGeneration','eventId','state']),
  messageDelta:obj({...eventFields,text:{type:'string'},messageId:id},['sessionId','processGeneration','eventId','text']),
  messageCompleted:obj({...eventFields,text:{type:'string'}},['sessionId','processGeneration','eventId','text']),
  permissionRequested:obj({...eventFields,requestId:id,title:str,question:str,options:arr(option,16),toolCall:obj({},[],true)},['sessionId','turnId','processGeneration','eventId','requestId','options']),
  questionRequested:obj({...eventFields,requestId:id,title:str,question:str,options:arr(option,16),toolCall:obj({},[],true)},['sessionId','turnId','processGeneration','eventId','requestId','question']),
  requestWithdrawn:obj({...eventFields,requestId:id,reason:str},['sessionId','processGeneration','eventId','requestId','reason']),
  turnCompleted:obj({...eventFields,stopReason:str},['sessionId','turnId','processGeneration','eventId','stopReason']),
  processExited:obj({...eventFields,code:{type:['integer','null']},signal:{type:['string','null']}},['sessionId','processGeneration','eventId','code']),
  error:obj({...eventFields,code:str,message:str,recoverable:{type:'boolean'}},['code','message','recoverable']),
  models:obj({...eventFields,models:arr(obj({modelId:id,name:str,description:{type:'string'},efforts:arr(obj({id,name:str},['id','name']),32)},['modelId','name']),1024),currentModelId:id,currentEffortId:id,efforts:arr(obj({id,name:str},['id','name']),32)},['sessionId','processGeneration','eventId','models','efforts']),
  capabilities:obj({...eventFields,capabilities:obj({},[],true)},['sessionId','processGeneration','eventId','capabilities']),
  planUpdated:obj({...eventFields,entries:arr(obj({},[],true))},['sessionId','processGeneration','eventId','entries']),
  usageUpdated:obj({...eventFields,usage:obj({},[],true)},['sessionId','processGeneration','eventId','usage'])
};
for(const name of ['toolStarted','toolUpdated','toolCompleted'])eventSchemas[name]=obj({...eventFields,toolCallId:id,title:str,status:str,content:arr(obj({},[],true)),result:{}},['sessionId','processGeneration','eventId','toolCallId','title','status']);
const agent = schema('agent-host',{oneOf:[
  ...Object.entries(requestSchemas).map(([method,params])=>obj({jsonrpc:{const:'2.0'},id:{type:['string','integer']},method:{const:method},params},['jsonrpc','method','params',...(['session.cancel','shutdown'].includes(method)?[]:['id'])])),
  ...Object.entries(eventSchemas).map(([method,params])=>obj({jsonrpc:{const:'2.0'},method:{const:method},params},['jsonrpc','method','params'])),
  obj({jsonrpc:{const:'2.0'},id:{type:['string','integer']},result:obj({},[],true)},['jsonrpc','id','result']),
  obj({jsonrpc:{const:'2.0'},id:{type:['string','integer']},error:obj({code:{type:'integer'},message:str,data:obj({},[],true)},['code','message'])},['jsonrpc','id','error'])
]});
const wire = schema('wire',{$defs:{
  AutomationRequest:obj({apiVersion:{const:'1'},requestId:id,clientId:id,operation:id,arguments:obj({},[],true)},['apiVersion','requestId','clientId','operation','arguments']),
  AutomationError:result.properties.error,
  AutomationResult:{...result,properties:{...result.properties,error:ref('AutomationError')}},
  InstanceDescriptor:obj({instanceId:id,pid:{type:'integer',minimum:1},startedAt:str,apiVersion:{const:'1'},url:str,token:str,appVersion:str},['instanceId','pid','startedAt','apiVersion','url','token','appVersion']),
  AccessGrant:obj({credential:str,url:str,instanceId:id,projectSessionId:id,clientId:id,grantId:id,expiresAt:str,processGeneration:{type:'integer',minimum:0},mode:field(['read_only','preview','apply'])},['credential','url','instanceId','projectSessionId'])
},oneOf:['AutomationRequest','AutomationResult','InstanceDescriptor','AccessGrant'].map(ref)});
delete wire.$defs.AutomationResult.$schema;delete wire.$defs.AutomationResult.$id;
const files = {'edit.schema.json':edit,'results.schema.json':result,'agent-host.schema.json':agent,'wire.schema.json':wire,'tools.json':{apiVersion:'1',tools:definitions},'errors.json':{apiVersion:'1',codes:errors}};
await mkdir(out,{recursive:true});
for(const [name,data] of Object.entries(files)){
  const content=JSON.stringify(data,null,2)+'\n';
  if(process.argv.includes('--check')){if(await readFile(resolve(out,name),'utf8')!==content)throw Error(`Stale contract: ${name}`);}
  else await writeFile(resolve(out,name),content);
}
// Both language DTOs are generated from the saved JSON schemas, rather than a second field list.
const wireSource=JSON.parse(await readFile(resolve(out,'wire.schema.json'),'utf8'));
const editSource=JSON.parse(await readFile(resolve(out,'edit.schema.json'),'utf8'));
const hostSource=JSON.parse(await readFile(resolve(out,'agent-host.schema.json'),'utf8'));
function tsType(s){
  if(s.$ref)return s.$ref.split('/').at(-1);
  if(s.const!==undefined)return JSON.stringify(s.const);
  if(s.enum)return s.enum.map(x=>JSON.stringify(x)).join(' | ');
  if(s.oneOf||s.anyOf)return (s.oneOf||s.anyOf).map(tsType).join(' | ');
  if(Array.isArray(s.type))return s.type.map(x=>tsType({...s,type:x})).join(' | ');
  if(s.type==='array')return `Array<${tsType(s.items)}>`;
  if(s.type==='object')return `{ ${Object.entries(s.properties||{}).map(([name,p])=>`${JSON.stringify(name)}${s.required?.includes(name)?'':'?'}: ${tsType(p)};`).join(' ')}${s.additionalProperties?' [key: string]: unknown;':''} }`;
  if(s.type==='integer'||s.type==='number')return 'number';
  if(s.type==='boolean')return 'boolean';if(s.type==='string')return 'string';if(s.type==='null')return 'null';return 'unknown';
}
const types = `// Generated from contracts/automation/v1 JSON schemas. Run npm run contracts.\nexport const API_VERSION = ${JSON.stringify(wireSource.$defs.AutomationRequest.properties.apiVersion.const)} as const;\nexport type ErrorCode = ${tsType(wireSource.$defs.AutomationError.properties.code)};\nexport type EditOperationType = ${tsType({enum:editSource.properties.operations.items.oneOf.map(x=>x.properties.type.const)})};\n${Object.entries(wireSource.$defs).map(([name,s])=>`export type ${name} = ${tsType(s)};`).join('\n')}\nexport type EditOperation = ${tsType(editSource.properties.operations.items)};\nexport type EditBatch = ${tsType(editSource)};\nexport type HostMethod = ${tsType({enum:hostSource.oneOf.filter(x=>x.required?.includes('id')&&x.properties.method).map(x=>x.properties.method.const).concat(['session.cancel','shutdown'])})};\nexport type HostNotification = ${tsType({enum:notifications})};\n`;
const typePath = resolve(root,'tools/openutau-ai/packages/contracts/generated.ts');
await mkdir(resolve(root,'tools/openutau-ai/packages/contracts'),{recursive:true});
if(process.argv.includes('--check')){if(await readFile(typePath,'utf8')!==types)throw Error('Stale generated TS contracts');}
else await writeFile(typePath,types);
const csharpNames={AutomationRequest:'AutomationWireRequest',AutomationError:'AutomationWireError',AutomationResult:'AutomationWireResult',InstanceDescriptor:'AutomationWireDescriptor',AccessGrant:'AutomationWireGrant'};
const pascal=value=>value[0].toUpperCase()+value.slice(1);
function csType(s){
  if(s.$ref)return csharpNames[s.$ref.split('/').at(-1)];
  if(s.const!==undefined||s.enum||s.type==='string')return 'string';
  if(s.type==='boolean')return 'bool';
  if(s.type==='integer')return 'long';if(s.type==='number')return 'double';
  if(s.type==='array')return `${csType(s.items)}[]`;
  if(s.type==='object'&&!Object.keys(s.properties||{}).length)return 'Dictionary<string, JsonElement>';
  return 'JsonElement';
}
const csHeader='// <auto-generated> Generated from contracts/automation/v1/wire.schema.json. </auto-generated>\n#nullable enable\nusing System.Collections.Generic;\nusing System.Text.Json;\nusing System.Text.Json.Serialization;\n\nnamespace OpenUtau.Core.Automation.Generated {\n';
const csClasses=Object.entries(wireSource.$defs).map(([name,s])=>{
  const fields=Object.entries(s.properties).map(([field,p])=>{
    const required=s.required?.includes(field);const type=csType(p);
    return `        [JsonPropertyName(${JSON.stringify(field)})]\n        public ${required?'required ':''}${type}${required?'':'?'} ${pascal(field)} { get; init; }`;
  });
  if(s.additionalProperties)fields.push('        [JsonExtensionData]\n        public Dictionary<string, JsonElement>? AdditionalData { get; init; }');
  return `    public sealed record ${csharpNames[name]} {\n${fields.join('\n')}\n    }`;
}).join('\n\n');
const cs=csHeader+`    public static class AutomationWireContract { public const string ApiVersion = ${JSON.stringify(wireSource.$defs.AutomationRequest.properties.apiVersion.const)}; }\n\n`+csClasses+'\n}\n';
await mkdir(resolve(out,'generated'),{recursive:true});const csPath=resolve(out,'generated/AutomationWire.g.cs');
if(process.argv.includes('--check')){if(await readFile(csPath,'utf8')!==cs)throw Error('Stale generated C# wire contracts');}else await writeFile(csPath,cs);

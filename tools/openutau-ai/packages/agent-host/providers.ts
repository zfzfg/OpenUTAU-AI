import { AutomationError } from '../contracts/errors.js';
import { locateExecutable, executableSpec, versionOf } from './process.js';

export interface ProviderBackend {id:string;name:string;adapterCommand?:string;cliCommand:string;nativeArgs:string[];cliEnv?:string;questionRpc:boolean;musicTools:'yes'|'probe'|'no';}
export const PROVIDERS:ProviderBackend[]=[
  {id:'codex',name:'Codex',adapterCommand:'codex-acp',cliCommand:'codex',nativeArgs:[],cliEnv:'CODEX_PATH',questionRpc:false,musicTools:'yes'},
  {id:'claude',name:'Claude',adapterCommand:'claude-agent-acp',cliCommand:'claude',nativeArgs:[],cliEnv:'CLAUDE_CODE_EXECUTABLE',questionRpc:false,musicTools:'yes'},
  {id:'grok',name:'Grok',cliCommand:'grok',nativeArgs:['agent','stdio'],questionRpc:true,musicTools:'yes'},
  {id:'gemini',name:'Gemini / Antigravity',cliCommand:'gemini',nativeArgs:['--acp'],questionRpc:false,musicTools:'probe'},
  {id:'muse',name:'Muse',cliCommand:'muse',nativeArgs:[],questionRpc:false,musicTools:'no'}
];
export function provider(id:unknown):ProviderBackend{const entry=PROVIDERS.find(x=>x.id===id);if(!entry)throw new AutomationError('INVALID_ARGUMENT','Choose a known provider.');return entry;}
export async function providerLaunch(backend:ProviderBackend,options:Record<string,any>){
  if(backend.musicTools!=='yes')throw new AutomationError('UNSUPPORTED_CAPABILITY','This provider has no verified OpenUtau tool round and cannot control the editor.');
  const cliPath=await locateExecutable(backend.cliCommand,options.cliPath);
  const adapterPath=backend.adapterCommand?await locateExecutable(backend.adapterCommand,options.adapterPath):cliPath;
  // Pass the consciously selected CLI path: adapter-packaged fallback binaries are never selected by the host.
  const env={...process.env,...(backend.cliEnv?{[backend.cliEnv]:cliPath}:{})};
  const spec=await executableSpec(adapterPath,backend.nativeArgs,env);
  return {spec,cliPath,adapterPath};
}
export async function probeProvider(options:Record<string,any>){
  const backend=provider(options.providerId);
  const capabilities={musicTools:backend.musicTools,questionRpc:backend.questionRpc,authentication:'unknown',nativeFileAndShellIsolation:'unknown'};
  try{
    const cliPath=await locateExecutable(backend.cliCommand,options.cliPath);const cliVersion=await versionOf(cliPath);
    if(backend.adapterCommand){
      try{const adapterPath=await locateExecutable(backend.adapterCommand,options.adapterPath);return {providerId:backend.id,status:'found',cliPath,cliVersion,adapterPath,adapterVersion:await versionOf(adapterPath),capabilities,message:'CLI and adapter found. Authentication and ACP capabilities are checked on connection.'};}
      catch{return {providerId:backend.id,status:'adapter_missing',cliVersion,capabilities,message:'Configure an already installed ACP adapter path.'};}
    }
    return {providerId:backend.id,status:'found',cliPath,cliVersion,capabilities,message:'Program found; ACP handshake checks compatibility on connection.'};
  }catch{return {providerId:backend.id,status:'cli_missing',capabilities,message:'Install or configure the provider CLI explicitly. OpenUtau does not install it automatically.'};}
}
export interface ModelOption {modelId:string;name:string;description?:string;efforts?:{id:string;name:string}[];wireIds?:Record<string,string>;}
export function sessionCatalog(data:Record<string,any>){
  const configs=Array.isArray(data.configOptions)?data.configOptions:[];
  const modelConfig=configs.find((x:any)=>x.category==='model'||x.id==='model');
  const effortConfig=configs.find((x:any)=>x.category==='thought_level'||x.id==='effort'||x.id==='reasoning_effort');
  const models:ModelOption[]=[];
  const available=Array.isArray(data.models?.availableModels)?data.models.availableModels:(modelConfig?.options||[]).map((x:any)=>({...x,modelId:x.value}));
  const configEfforts=(effortConfig?.options||[]).filter((x:any)=>typeof x.value==='string').map((x:any)=>({id:x.value,name:x.name||x.value}));
  let currentModelId=data.models?.currentModelId||modelConfig?.currentValue;
  let currentEffortId=effortConfig?.currentValue;
  for(const item of available){
    if(typeof item.modelId!=='string')continue;
    const composite=/^(.*)\[([^\[\]]+)\]$/.exec(item.modelId);
    const modelId=composite?composite[1]:item.modelId;
    let model=models.find(x=>x.modelId===modelId);
    if(!model){model={modelId,name:item.name||modelId,...(item.description?{description:item.description}:{}),efforts:[...configEfforts],wireIds:{}};models.push(model);}
    if(composite){const effort=composite[2];if(!model.efforts!.some(x=>x.id===effort))model.efforts!.push({id:effort,name:effort});model.wireIds![effort]=item.modelId;model.name=model.name.replace(new RegExp(`\\s*\\(${effort}\\)$`),'');}
    const meta=item._meta||{};
    for(const option of meta.reasoningEfforts||[]){if(typeof option.value==='string'&&!model.efforts!.some(x=>x.id===option.value))model.efforts!.push({id:option.value,name:option.name||option.value});}
    if(meta.reasoningEffort&&currentModelId===item.modelId)currentEffortId=meta.reasoningEffort;
  }
  const compositeCurrent=typeof currentModelId==='string'?/^(.*)\[([^\[\]]+)\]$/.exec(currentModelId):null;
  if(compositeCurrent){currentModelId=compositeCurrent[1];currentEffortId=compositeCurrent[2];}
  return {models,currentModelId,currentEffortId,efforts:models.find(x=>x.modelId===currentModelId)?.efforts||configEfforts,modelConfigId:modelConfig?.id,effortConfigId:effortConfig?.id,modes:data.modes?.availableModes||[],currentModeId:data.modes?.currentModeId};
}
export function providerError(error:unknown):{code:string;message:string;recoverable:boolean}{
  const text=error instanceof Error?error.message:'Provider request failed.';
  const known=error instanceof AutomationError?error.code:'PROVIDER_ERROR';
  const code=/auth|login|credential|unauthorized/i.test(text)?'AUTH_REQUIRED':/quota|rate.?limit|credits|usage limit/i.test(text)?'QUOTA_EXHAUSTED':known;
  const sanitized=text.replace(/Bearer\s+\S+/gi,'Bearer [redacted]').replace(/((?:api[_-]?key|token|credential|secret)\s*[:=]\s*)["']?[^\s,"'}]+/gi,'$1[redacted]');
  return {code,message:code==='AUTH_REQUIRED'?'The provider needs authentication in its own CLI.':code==='QUOTA_EXHAUSTED'?'The provider reported a quota or rate limit.':sanitized.slice(0,1024),recoverable:true};
}

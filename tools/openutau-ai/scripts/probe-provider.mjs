// Explicit, inference-free provider inventory. Normal tests never run this script.
import { provider,providerLaunch,probeProvider,startProcess,stopOwnedProcess,RpcPeer } from '../dist/test-library.js';
const args=process.argv.slice(2);const value=name=>args.includes(name)?args[args.indexOf(name)+1]:undefined;
const options={providerId:value('--provider'),cliPath:value('--cli'),adapterPath:value('--adapter')};
const info=await probeProvider(options);
if(args.includes('--initialize-only')&&info.status==='found'){
  const launch=await providerLaunch(provider(options.providerId),options);const child=await startProcess(launch.spec,process.cwd());const peer=new RpcPeer(child.stdout,child.stdin);child.stderr.on('data',()=>{});
  try{const result=await peer.request('initialize',{protocolVersion:1,clientInfo:{name:'OpenUtau-inference-free-probe',version:'1.0'},clientCapabilities:{fs:{readTextFile:false,writeTextFile:false},terminal:false}},15000);info.handshake={protocolVersion:result.protocolVersion,agentInfo:result.agentInfo,agentCapabilities:result.agentCapabilities};}
  catch(error){info.handshake={error:error.code||'PROVIDER_ERROR',message:error.message};}
  finally{await stopOwnedProcess(child);peer.close();}
}
process.stdout.write(JSON.stringify(info,null,2)+'\n');

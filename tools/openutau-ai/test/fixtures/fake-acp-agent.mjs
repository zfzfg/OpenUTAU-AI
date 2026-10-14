import readline from 'node:readline';
import { randomUUID } from 'node:crypto';
import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
let nativeSessionId='';let currentModelId='fixture-model[low]';let currentEffort='low';let servers=[];let promptId;let cardId;let cardKind;let ignoreCancel=false;
const write=frame=>process.stdout.write(JSON.stringify({jsonrpc:'2.0',...frame})+'\n');
const result=(id,value)=>write({id,result:value});const update=value=>write({method:'session/update',params:{sessionId:nativeSessionId,update:value}});
const models=()=>({models:{currentModelId,availableModels:[{modelId:'fixture-model[low]',name:'Fixture model (low)'},{modelId:'fixture-model[high]',name:'Fixture model (high)'},{modelId:'another-model',name:'Another model'}]}});
async function mcpClient(name){const entry=servers.find(x=>x.name===name);if(!entry)throw Error('MCP injection absent');const client=new Client({name:'fixture-acp',version:'1'},{capabilities:{}});const env={...Object.fromEntries(Object.entries(process.env).filter(([,value])=>value!==undefined)),...Object.fromEntries(entry.env.map(x=>[x.name,x.value]))};await client.connect(new StdioClientTransport({command:entry.command,args:entry.args,env,stderr:'pipe'}));return client;}
async function receive(frame){
  if(frame.method==='initialize'){return result(frame.id,{protocolVersion:1,agentInfo:{name:'OpenUtau fake ACP',version:'1.0'},agentCapabilities:{loadSession:true,mcpCapabilities:{},sessionCapabilities:{list:{}}}});}
  if(frame.method==='session/new'||frame.method==='session/load'){
    servers=frame.params.mcpServers;
    if(servers.filter(x=>x.name==='openutau').length!==1||servers.filter(x=>x.name==='openutau-assistant').length!==1)throw Error('duplicate or missing MCP injection');
    if(servers.some(x=>!Array.isArray(x.env)||!Array.isArray(x.args)))throw Error('invalid ACP v1 MCP shape');
    nativeSessionId=frame.params.sessionId||randomUUID();
    if(frame.method==='session/load')update({sessionUpdate:'agent_message_chunk',content:{type:'text',text:'Resumed conversation.'}});
    return result(frame.id,{sessionId:nativeSessionId,...models()});
  }
  if(frame.method==='session/set_model'){currentModelId=frame.params.modelId;return result(frame.id,models());}
  if(frame.method==='session/set_config_option'){currentEffort=frame.params.value;return result(frame.id,{configOptions:[{id:'effort',category:'thought_level',currentValue:currentEffort,options:[{value:'low',name:'Low'},{value:'high',name:'High'}]}]});}
  if(frame.method==='session/list')return result(frame.id,{sessions:[{sessionId:nativeSessionId,cwd:frame.params.cwd,title:'Fixture history'}]});
  if(frame.method==='session/prompt'){
    promptId=frame.id;
    const text=frame.params.prompt.map(x=>x.text||'').join('').split('User request:\n').at(-1);
    if(text==='malformed'){process.stdout.write('{broken\n');return;}
    if(text==='oversize'){process.stdout.write('a'.repeat(2*1024*1024+1));return;}
    if(text==='eof'){process.exit();}
    if(text==='ignore_cancel'){ignoreCancel=true;return;}
    if(text==='wait')return;
    if(text==='stream-wait'){update({sessionUpdate:'agent_message_chunk',content:{type:'text',text:'Streaming begun'}});return;}
    if(text==='tool-wait'){update({sessionUpdate:'tool_call',toolCallId:'waiting-tool',title:'Waiting for tool',status:'in_progress'});return;}
    if(text==='grok-interview'){
      cardId='interview';write({id:cardId,method:'x.ai/ask_user_question',params:{sessionId:nativeSessionId,questions:[{question:'Language?',header:'Language',multiSelect:false,options:[{label:'German'},{label:'English'}]},{question:'Tempo?',header:'Tempo',multiSelect:false,options:[{label:'120'},{label:'140'}]}]}});return;
    }
    if(text==='permission'||text==='native-question'){
      cardKind=text;cardId='card_1';write({id:cardId,method:text==='permission'?'session/request_permission':'session/request_question',params:{sessionId:nativeSessionId,toolCall:{title:'Read file',toolCallId:'tool_1'},question:'What lyric?',options:[{optionId:'allow_once',name:'Allow once',kind:'allow_once'},{optionId:'reject_once',name:'Reject once',kind:'reject_once'}]}});return;
    }
    if(text==='helper-question'){
      const client=await mcpClient('openutau-assistant');try{
        const ask=await client.callTool({name:'ask_user',arguments:{question:'Which lyric?',options:[{optionId:'yes',name:'Use the lyric'}]}});
        const questionId=ask.structuredContent.questionId;
        const answer=await client.callTool({name:'read_answer',arguments:{questionId,timeoutMs:30000}});
        update({sessionUpdate:'agent_message_chunk',content:{type:'text',text:JSON.stringify(answer.structuredContent)}});
      }finally{await client.close();}return result(frame.id,{stopReason:'end_turn'});
    }
    if(text==='tool'){
      const client=await mcpClient('openutau');try{
        update({sessionUpdate:'tool_call',toolCallId:'music_tool',title:'Read OpenUtau project',status:'in_progress'});
        const tool=await client.callTool({name:'openutau_get_project',arguments:{}});
        update({sessionUpdate:'tool_call_update',toolCallId:'music_tool',status:tool.isError?'failed':'completed',rawOutput:tool.structuredContent});
      }finally{await client.close();}
    }
    const message=JSON.stringify({jsonrpc:'2.0',method:'session/update',params:{sessionId:nativeSessionId,update:{sessionUpdate:'agent_message_chunk',content:{type:'text',text:'Hallo 🎵 Grüße'}}}})+'\n';
    const bytes=Buffer.from(message);const offset=bytes.indexOf(Buffer.from('🎵'))+1;process.stdout.write(bytes.subarray(0,offset));await new Promise(resolve=>setTimeout(resolve,2));process.stdout.write(bytes.subarray(offset));
    return result(frame.id,{stopReason:'end_turn'});
  }
  if(frame.method==='session/cancel'){
    if(frame.id!==undefined)throw Error('cancel must be a notification');if(ignoreCancel)return;
    const id=promptId;promptId=undefined;if(id!==undefined)result(id,{stopReason:'cancelled'});return;
  }
  if(frame.id===cardId&&!frame.method){update({sessionUpdate:'agent_message_chunk',content:{type:'text',text:JSON.stringify(frame.result)}});result(promptId,{stopReason:'end_turn'});cardId=undefined;return;}
  if(frame.id!==undefined)write({id:frame.id,error:{code:-32601,message:'Unknown fixture method'}});
}
const lines=readline.createInterface({input:process.stdin,crlfDelay:Infinity});lines.on('line',line=>{void receive(JSON.parse(line)).catch(error=>{write({method:'session/update',params:{sessionId:nativeSessionId,update:{sessionUpdate:'agent_message_chunk',content:{type:'text',text:error.message}}}});if(promptId)write({id:promptId,error:{code:-32000,message:error.message}});});});lines.on('close',()=>process.exit());

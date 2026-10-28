const byId = id => document.getElementById(id);
const node = (tag, cls, text) => { const n = document.createElement(tag); if (cls) n.className = cls; if (text !== undefined) n.textContent = text; return n; };
const clear = element => element.replaceChildren();
let state = {}, products = [], orders = [], proposals = [], audit = [], connectors = [], roles = [], agents = [], tasks = [];
let selectedProduct = null, activeOrder = null, activeProposal = null, checkoutOrder = null, followupPolicy = {};
const titles = {
  overview: ['Overview','Monitor your commerce platform, orders, and approvals at a glance'],
  orders: ['Orders','Review sandbox orders and payment status'],
  detail: ['Order details','Payment information and the associated activity trail'],
  approvals: ['Approvals','Review and decide proposed actions'],
  policies: ['Policies','Control agent actions and approval requirements'],
  roles: ['Roles','Define human and agent responsibilities'],
  agents: ['Agents and sub-agents','Define agent roles and delegated permissions'],
  audit: ['Audit Log','Explore action history, actors, decisions and reasons'],
  catalog: ['Catalog','Local sample products for sandbox checkout'],
  connectors: ['Connectors','Provider roles and verification status']
};
function notify(text) { const n=byId('notice'); n.textContent=text; n.hidden=false; }
async function api(url,options) {
  const requestOptions = options ? { ...options, headers: { ...(options.headers || {}), ...((options.method && options.method !== "GET") ? { "X-CSRF-Token": csrfToken } : {}) } } : undefined;
  const response=await fetch(url,requestOptions); const body=await response.json().catch(()=>({}));
  if (!response.ok) {
    if (response.status === 401 && url !== '/api/auth/login' && url !== '/api/auth/session') { byId('logoutButton').hidden = true; void startSession(); }
    throw Error(body.error || body.detail || 'Request failed ('+response.status+').');
  }
  return body;
}
function post(url,data,headers={}) { return api(url,{method:'POST',headers:{'Content-Type':'application/json',...headers},body:JSON.stringify(data)}); }
function action(text,fn,cls='button outline') { const b=node('button',cls,text); b.type='button'; b.addEventListener('click',fn); return b; }
function badge(text,cls='neutral') {return node('span','badge '+cls,text);}
function empty(target,text) {target.append(node('div','empty',text));}
function displayId(id) {return id ? id.slice(0,10) : 'unknown';}
function money(amount,currency) { return Number(amount).toFixed(2)+' '+currency; }
function when(value) {const d=new Date(value);return Number.isNaN(d.getTime()) ? 'Unknown time' : d.toLocaleString();}
function navigate(view) {
  if (!titles[view]) return;
  document.querySelectorAll('.view').forEach(section=>section.hidden=section.id!=='view-'+view);
  document.querySelectorAll('.nav-item').forEach(item=>item.classList.toggle('active',item.dataset.view===(view==='detail'?'orders':view)));
  byId('pageTitle').textContent=titles[view][0];byId('pageSubtitle').textContent=titles[view][1];
  if(view==='detail')renderDetail();
  window.scrollTo({top:0,behavior:'auto'});
}
document.querySelectorAll('[data-view]').forEach(b=>b.addEventListener('click',()=>navigate(b.dataset.view)));
document.querySelectorAll('[data-go]').forEach(b=>b.addEventListener('click',()=>navigate(b.dataset.go)));
async function refresh() {
  try {
    [state,products,orders,proposals,audit,connectors,roles,agents,tasks,followupPolicy]=await Promise.all(['/api/status','/api/products','/api/orders','/api/proposals','/api/audit','/api/connectors','/api/roles','/api/agents','/api/tasks','/api/policies/followup'].map(url=>api(url)));
    renderSystem();renderOrders();renderApprovals();renderAudit();renderProducts();renderConnectors();renderInsights();renderIdentities();renderTasks();renderPolicy();
    if(activeOrder && !byId('view-detail').hidden)renderDetail();
  } catch(error){notify(error.message)}
}
function renderSystem() {
  const target=byId('systemStatus');clear(target);
  const summary=node('div','status-summary');summary.append(node('span','dot green'),document.createTextNode('Application responding'));target.append(summary,node('p','muted','Provider access has not been verified.'));
  const box=node('div','status-box');
  function pair(label,value){const d=node('div');d.append(node('small','',label),node('strong','',value));return d;}
  box.append(pair('Environment','Sandbox mode'),pair('Backend','Responding'),pair('PayPal API',state.paypal?'Configured, not verified':'Not configured'));target.append(box);
}
function orderRow(order) {
  const row=node('div','list-row'), icon=node('div','row-icon','P'), info=node('div','row-main');
  info.append(node('strong','','Order '+displayId(order.id)),node('small','',order.productName+' - '+money(order.price,order.currency)));
  row.append(icon,info,badge(order.status==='PAID'?'Paid':'Created',order.status==='PAID'?'green':'orange'));
  row.append(action('Details',()=>{activeOrder=order.id;navigate('detail')},'link'));return row;
}
function proposalRow(p) {
  const row=node('div','list-row'),icon=node('div','row-icon','!'),info=node('div','row-main');
  info.append(node('strong','','Order '+displayId(p.orderId)+' - internal follow-up'),node('small','',p.reason));
  row.append(icon,info,badge(p.status==='PENDING'?'Pending':p.status==='REJECTED'?'Rejected':'Task created',p.status==='PENDING'?'orange':p.status==='TASK_CREATED'?'green':'neutral'));
  if(p.status==='PENDING')row.append(action('Review',()=>openApproval(p),'button outline'));
  return row;
}
function auditRow(a) {
  const row=node('div','audit-row'), event=node('div','event'), detail=node('div','detail');
  event.append(node('span','dot '+(a.event.startsWith('approval')?'orange':a.event.startsWith('proposal')?'purple':'blue')),document.createTextNode(a.event));
  detail.append(document.createTextNode(a.reason),node('span','audit-actor','Actor: '+a.actor+' | Source: '+a.source+(a.reference?' | Reference: '+a.reference:'')));
  row.append(event,detail,node('time','',when(a.at)));return row;
}
function renderAudit() {
  for(const [id,items] of [['recentAudit',audit.slice(0,6)],['auditList',audit]]) {
    const target=byId(id);clear(target);if(!items.length){empty(target,'Workflow events and decisions will appear here.');continue}items.forEach(a=>target.append(auditRow(a)));
  }
}
async function downloadArtifact(url, filename) {
  const response = await fetch(url);
  if (!response.ok) {
    const body = await response.json().catch(() => ({}));
    throw Error(body.error || body.detail || 'The artifact could not be downloaded.');
  }
  const link = document.createElement('a');
  link.href = URL.createObjectURL(await response.blob());
  link.download = filename;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(link.href), 0);
}
byId('downloadAuditExport').addEventListener('click', async () => {
  try { await downloadArtifact('/api/audit/export', 'paypal360-audit.ndjson'); notify('Audit export downloaded. Archive it outside the application.'); }
  catch (error) { notify(error.message); }
});
byId('downloadAuditAnchor').addEventListener('click', async () => {
  try { await downloadArtifact('/api/audit/anchor', 'paypal360-audit-anchor.json'); notify('Audit checkpoint downloaded. Store it with the external audit archive.'); }
  catch (error) { notify(error.message); }
});
function connectorCard(c) {
  const card=node('div','registry-card'), symbol=node('div','row-icon',c.provider.startsWith('PayPal')?'P':c.provider.startsWith('Google')?'G':'•');
  card.append(symbol,node('strong','',c.provider),node('small','',c.role),node('div','state',c.status.replaceAll('-',' ')),node('small','',c.detail));return card;
}
function renderInsights() {
  const target=byId('insights');clear(target);
  if(!proposals.length) return empty(target,state.ai?'No AI reviews yet.':'AI review is not configured.');
  proposals.slice(0,2).forEach(p=>{const item=node('div','insight');item.append(node('strong','','Proposed follow-up'),node('small','',p.reason));target.append(item)});
}
function renderDetail() {
  const target=byId('orderDetail');clear(target);const order=orders.find(o=>o.id===activeOrder);
  if(!order)return empty(target,'Order is no longer available in the local session.');
  const title=node('div','detail-title');title.append(node('h2','','Order '+displayId(order.id)),badge(order.status==='PAID'?'Paid':'Created',order.status==='PAID'?'green':'orange'));target.append(title);
  const grid=node('div','detail-grid'),left=node('div','detail-column'),right=node('div','detail-column');
  const items=node('article','card'), heading=node('div','card-heading');heading.append(node('h2','','Order Items'));items.append(heading);
  const item=node('div','detail-item'),product=node('div');product.append(node('strong','',order.productName),node('small','','Product ID: '+order.productId+' | Quantity: 1'));
  item.append(product,node('strong','',money(order.price,order.currency)));items.append(item,node('div','detail-total','Total: '+money(order.price,order.currency)));left.append(items);
  const review=node('article','card');review.append(node('h2','','AI Review'),node('p','muted','Ask the agent to propose an internal follow-up. The approval gate will review it.'));
  const eligible=agents.filter(agent=>agent.effectivePermissions.includes('read_orders')&&agent.effectivePermissions.includes('propose_followup'));
  const agentLabel=node('label','','Agent'),agentSelect=node('select');
  for(const agent of eligible){const option=node('option','',agent.name);option.value=agent.id;agentSelect.append(option)}
  agentLabel.append(agentSelect);review.append(agentLabel);
  const b=action('Ask AI to review',async()=>{b.disabled=true;try{const result=await post('/api/orders/'+order.id+'/review',{agentId:agentSelect.value});await refresh();notify(result.status==='TASK_CREATED'?'Internal follow-up created automatically under the configured policy.':'AI proposal created and sent to the approval queue.');navigate('approvals')}catch(e){notify(e.message)}finally{b.disabled=false}},'button primary');
  b.disabled=order.status!=='PAID'||!state.ai||!eligible.length;review.append(b);
  if(b.disabled)review.append(node('p','hint',order.status!=='PAID'?'A completed payment is required.':!state.ai?'Configure the AI provider to use this feature.':'Define an agent with read orders and propose follow-up permissions.'));left.append(review);
  const activity=node('article','card');activity.append(node('h2','','Order Activity'));const matches=audit.filter(a=>a.orderId===order.id);
  if(!matches.length)empty(activity,'No audit events for this order.');else matches.forEach(a=>{const line=node('div','timeline-event');line.append(node('strong','',a.event),node('p','',a.reason),node('small','',a.actor+' | '+when(a.at)));activity.append(line)});right.append(activity);
  const metadata=node('article','card');metadata.append(node('h2','','Payment & Metadata'));const data=node('div','metadata');
  for(const [label,value] of [['Gateway','PayPal sandbox'],['Provider order ID',order.providerOrderId],['Payment status',order.status],['Customer details','Not collected']])data.append(node('span','',label),node('strong','',value));
  metadata.append(data);right.append(metadata);grid.append(left,right);target.append(grid);
}
byId('closeDialog').addEventListener('click',()=>byId('approvalDialog').close());
byId('decisionForm').addEventListener('submit',event=>event.preventDefault());
async function decide(decision) {
  const reason=byId('decisionReason').value.trim(),error=byId('dialogError');
  if(!reason){error.textContent='Enter the decision reason.';error.hidden=false;return}
  byId('approve').disabled=true;byId('reject').disabled=true;
  try {
    await post('/api/proposals/'+activeProposal+'/decision',{decision,reason});
    byId('approvalDialog').close();await refresh();notify(decision==='approve'?'Internal follow-up created. No external message was sent.':'Proposal rejected.');
  }catch(e){error.textContent=e.message;error.hidden=false}finally{byId('approve').disabled=false;byId('reject').disabled=false}
}
byId('approve').addEventListener('click',()=>decide('approve'));byId('reject').addEventListener('click',()=>decide('reject'));
async function checkout(product) {
  selectedProduct=product;checkoutOrder=null;const checkoutStorageKey='paypal360.checkout.'+product.id;const checkoutRequestId=sessionStorage.getItem(checkoutStorageKey)||crypto.randomUUID();sessionStorage.setItem(checkoutStorageKey,checkoutRequestId);byId('checkout').hidden=false;byId('checkoutProduct').textContent=product.name+' - '+money(product.price,product.currency);clear(byId('paypalButtons'));
  try {
    if(!window.paypal)await new Promise((resolve,reject)=>{const script=document.createElement('script');script.src='https://www.paypal.com/sdk/js?client-id='+encodeURIComponent(state.clientId)+'&currency='+encodeURIComponent(product.currency);script.onload=resolve;script.onerror=()=>reject(Error('Could not load PayPal checkout.'));document.head.append(script)});
    window.paypal.Buttons({
      createOrder:async()=>{const r=await post('/api/checkout',{productId:product.id,requestId:checkoutRequestId});checkoutOrder=r.orderId;return r.paypalOrderId},
      onApprove:async()=>{if(!checkoutOrder)throw Error('Order is missing.');await post('/api/checkout/'+checkoutOrder+'/capture',{});sessionStorage.removeItem(checkoutStorageKey);byId('checkout').hidden=true;await refresh();notify('Sandbox payment captured.');navigate('orders')},
      onError:error=>notify('PayPal checkout failed: '+(error.message||'Check sandbox configuration.'))
    }).render('#paypalButtons');
  }catch(error){notify(error.message)}
}
byId('closeCheckout').addEventListener('click',()=>{byId('checkout').hidden=true;clear(byId('paypalButtons'))});
byId('refresh').addEventListener('click',refresh);
// Effective agent permissions are currently enforced only for the AI review workflow.
const permissionCatalog = {
  agent: [['read_orders','Read orders'],['read_catalog','Read catalog'],['propose_followup','Propose internal follow-ups'],['capture_payment','Capture payments'],['refund_payment','Request refunds'],['fulfill_order','Request fulfillment'],['publish_product','Request product publication']],
  human: [['read_orders','Read orders'],['read_catalog','Read catalog'],['read_audit','Read audit'],['review_proposals','Review proposals'],['approve_proposals','Approve proposals'],['manage_policies','Manage policies'],['manage_users','Manage users'],['manage_definitions','Manage role and agent definitions'],['execute_actions','Execute merchant actions']]
};
function renderPermissionChoices() {
  const list=byId('permissionChoices');clear(list);
  for(const [value,label] of permissionCatalog[byId('roleKind').value]){
    const row=node('label'),checkbox=node('input');checkbox.type='checkbox';checkbox.value=value;checkbox.name='permission';row.append(checkbox,document.createTextNode(label));list.append(row);
  }
}
function renderIdentities(){
  const rolesTarget=byId('rolesList');clear(rolesTarget);
  if(!roles.length)empty(rolesTarget,'No roles defined yet.');
  for(const role of roles){
    const row=node('div','identity-entry');row.append(node('strong','',role.name),node('small','',role.kind==='human'?'Human role':'Agent role'));
    for(const p of role.permissions)row.append(badge(p.replaceAll('_',' '),'blue'));rolesTarget.append(row);
  }
  const roleSelect=byId('agentRole'),parentSelect=byId('agentParent'),previousRole=roleSelect.value,previousParent=parentSelect.value;
  clear(roleSelect);const placeholder=node('option','','Select an agent role');placeholder.value='';roleSelect.append(placeholder);
  for(const role of roles.filter(r=>r.kind==='agent')){const option=node('option','',role.name);option.value=role.id;roleSelect.append(option)}
  if(roles.some(r=>r.id===previousRole && r.kind==='agent'))roleSelect.value=previousRole;
  clear(parentSelect);const root=node('option','','None - root agent');root.value='';parentSelect.append(root);
  for(const agent of agents){const option=node('option','',agent.name);option.value=agent.id;parentSelect.append(option)}
  if(agents.some(a=>a.id===previousParent))parentSelect.value=previousParent;
  const agentsTarget=byId('agentsList');clear(agentsTarget);
  if(!agents.length)empty(agentsTarget,'No agents defined yet.');
  for(const agent of agents){
    const role=roles.find(r=>r.id===agent.roleId),parent=agents.find(a=>a.id===agent.parentId);
    const row=node('div','identity-entry');row.append(node('strong','',agent.name),node('small','','Role: '+(role?.name||'Unknown')+' | Parent: '+(parent?.name||'None - root agent')));
    if(agent.effectivePermissions.length)for(const p of agent.effectivePermissions)row.append(badge(p.replaceAll('_',' '),'blue'));
    else row.append(node('small','','No inherited permissions.'));
    agentsTarget.append(row);
  }
  renderPermissionPreview();
}
function renderPermissionPreview(){
  const target=byId('permissionPreview');clear(target);const role=roles.find(r=>r.id===byId('agentRole').value),parent=agents.find(a=>a.id===byId('agentParent').value);
  const effective=role ? role.permissions.filter(p=>!parent || parent.effectivePermissions.includes(p)) : [];
  target.append(node('span','',role ? 'Planned effective permissions: '+(effective.length?effective.map(p=>p.replaceAll('_',' ')).join(', '):'none') : 'Select an agent role to preview its permissions.'));
}
function definitionAttribution(form){
  return {reason:form.querySelector('.definition-reason').value.trim()};
}
async function submitDefinition(event,type){
  event.preventDefault();const form=event.currentTarget,attribution=definitionAttribution(form);
  if(!attribution.reason)return notify('Enter the reason for creating this definition.');
  let body;
  if(type==='roles'){
    const permissions=Array.from(byId('permissionChoices').querySelectorAll('input:checked')).map(input=>input.value);
    body={name:byId('roleName').value.trim(),kind:byId('roleKind').value,permissions,reason:attribution.reason};
  }else{
    body={name:byId('agentName').value.trim(),roleId:byId('agentRole').value,parentId:byId('agentParent').value||null,reason:attribution.reason};
  }
  const submit=form.querySelector('button[type=submit]');submit.disabled=true;
  try{await post('/api/'+type,body);await refresh();notify(type==='roles'?'Role definition created.':'Agent definition created.')}
  catch(error){notify(error.message)}finally{submit.disabled=false}
}
byId('roleKind').addEventListener('change',renderPermissionChoices);
byId('agentRole').addEventListener('change',renderPermissionPreview);byId('agentParent').addEventListener('change',renderPermissionPreview);
byId('roleForm').addEventListener('submit',event=>submitDefinition(event,'roles'));byId('agentForm').addEventListener('submit',event=>submitDefinition(event,'agents'));
renderPermissionChoices();



let approvalFilter = 'pending';

function addMetric(id, value) { byId(id).textContent = String(value); }
function table(headers) {
  const wrapper=node('div','table-scroll'), result=node('table','data-table'), head=node('thead'), row=node('tr');
  for (const label of headers) row.append(node('th','',label));
  head.append(row);result.append(head);wrapper.append(result);return {wrapper,result};
}
function orderWorkflowStatus(id) {
  const matches=proposals.filter(p=>p.orderId===id);
  if(!matches.length) return 'No proposal';
  if(matches.some(p=>p.status==='PENDING')) return 'Approval needed';
  return matches[0].status==='REJECTED'?'Rejected':'Task created';
}
function renderOrders() {
  const recent=byId('recentOrders');clear(recent);
  if(!orders.length)empty(recent,'No sandbox orders yet. Open the catalog to start.');
  else {const list=node('div','list-box');orders.slice(0,4).forEach(o=>list.append(orderRow(o)));recent.append(list)}
  addMetric('ordersTotal',orders.length);addMetric('ordersPaid',orders.filter(o=>o.status==='PAID').length);
  addMetric('ordersCreated',orders.filter(o=>o.status==='CREATED').length);addMetric('ordersPending',proposals.filter(p=>p.status==='PENDING').length);
  const target=byId('ordersList');clear(target);
  const query=byId('orderSearch').value.trim().toLowerCase();
  const matching=orders.filter(o=>(o.id+' '+o.productName+' '+o.providerOrderId).toLowerCase().includes(query));
  if(!matching.length)return empty(target,orders.length?'No orders match this search.':'No sandbox orders yet. Open the catalog to start.');
  const {wrapper,result}=table(['Order ID','Product','Provider','Amount','Payment state','Workflow status','']);
  const body=node('tbody');
  for(const o of matching){
    const row=node('tr'),id=node('td'),product=node('td'),provider=node('td'),amount=node('td'),payment=node('td'),workflow=node('td'),actions=node('td');
    id.append(node('strong','',displayId(o.id)));product.append(node('strong','',o.productName));provider.textContent='PayPal sandbox';amount.textContent=money(o.price,o.currency);
    payment.append(badge(o.status==='PAID'?'Captured':'Created',o.status==='PAID'?'green':'orange'));
    const workflowStatus=orderWorkflowStatus(o.id);workflow.append(badge(workflowStatus,workflowStatus==='Approval needed'?'orange':'neutral'));
    actions.append(action('Details',()=>{activeOrder=o.id;navigate('detail')},'link'));row.append(id,product,provider,amount,payment,workflow,actions);body.append(row);
  }
  result.append(body);target.append(wrapper,node('p','hint','Showing '+matching.length+' of '+orders.length+' orders.'));
}
function renderApprovals() {
  const recent=byId('recentApprovals');clear(recent);
  if(!proposals.length)empty(recent,'No approval requests yet.');
  else {const list=node('div','list-box');proposals.slice(0,2).forEach(p=>list.append(proposalRow(p)));recent.append(list)}
  addMetric('approvalPendingCount',proposals.filter(p=>p.status==='PENDING').length);
  addMetric('approvalApprovedCount',proposals.filter(p=>p.status==='TASK_CREATED').length);
  addMetric('approvalRejectedCount',proposals.filter(p=>p.status==='REJECTED').length);
  byId('approvalPendingTab').classList.toggle('active',approvalFilter==='pending');byId('approvalResolvedTab').classList.toggle('active',approvalFilter==='resolved');
  const target=byId('approvalsList');clear(target);
  const matching=proposals.filter(p=>approvalFilter==='pending'?p.status==='PENDING':p.status!=='PENDING');
  if(!matching.length)return empty(target,approvalFilter==='pending'?'No actions require approval.':'No resolved actions yet.');
  for(const p of matching){
    const card=node('article','card approval-card'),description=node('div','approval-summary'),details=node('div','approval-facts'),controls=node('div','approval-actions');
    const order=orders.find(o=>o.id===p.orderId);
    description.append(badge('Internal follow-up','neutral'),node('h3','','Order '+displayId(p.orderId)),node('p','',order?money(order.price,order.currency):'Order no longer available'),badge('follow-up-v'+p.policyVersion,'orange'));
    const status=node('div');status.append(node('small','','PAYMENT STATE'),node('strong','',order?(order.status==='PAID'?'Captured':'Created'):'Unavailable'));
    const evidence=node('div');evidence.append(node('small','','AI PROPOSAL'),node('p','',p.reason));details.append(status,evidence);
    controls.append(p.status==='PENDING'?action('Review & decide',()=>openApproval(p),'button primary'):badge(p.status==='REJECTED'?'Rejected':'Task created',p.status==='REJECTED'?'neutral':'green'));
    if(followupPolicy.mode==='human_only' && order?.status==='PAID') controls.append(action('Create manual task',async()=>{const description=window.prompt('Describe the internal follow-up task');if(!description?.trim())return;try{await post('/api/orders/'+order.id+'/tasks',{description:description.trim()});await refresh();notify('Manual internal task created.')}catch(e){notify(e.message)}},'button outline'));
    card.append(description,details,controls);target.append(card);
  }
}
function renderProducts() {
  addMetric('catalogTotal',products.length);const target=byId('products');clear(target);
  const query=byId('productSearch').value.trim().toLowerCase();const matching=products.filter(p=>(p.name+' '+p.id).toLowerCase().includes(query));
  if(!matching.length)return empty(target,products.length?'No products match this search.':'No products available.');
  const {wrapper,result}=table(['Product','Product ID','Price','Inventory','Channel sync','']);const body=node('tbody');
  for(const p of matching){
    const row=node('tr'),name=node('td'),id=node('td'),price=node('td'),stock=node('td'),channel=node('td'),actions=node('td');
    name.append(node('strong','',p.name));id.textContent=p.id;price.textContent=money(p.price,p.currency);stock.textContent='Not tracked';channel.append(badge('Unverified','neutral'));
    const b=action('Buy in sandbox',()=>checkout(p),'button primary');b.disabled=!state.paypal;actions.append(b);row.append(name,id,price,stock,channel,actions);body.append(row);
  }
  result.append(body);target.append(wrapper,node('p','hint','Showing '+matching.length+' of '+products.length+' products.'));
  if(!state.paypal)target.append(node('p','hint','Configure PayPal sandbox credentials to use checkout.'));
}
function renderConnectors() {
  addMetric('connectorTotal',connectors.length);addMetric('connectorAvailable',connectors.filter(c=>c.status==='available').length);
  addMetric('connectorUnverified',connectors.filter(c=>c.status==='unverified'||c.status==='configured-not-verified').length);
  byId('connectorPayPal').textContent=state.paypal?'Configured':'Not configured';
  for(const [id,items] of [['registryPreview',connectors.slice(0,3)],['connectorsList',connectors]]) {
    const t=byId(id);clear(t);items.forEach(c=>t.append(connectorCard(c)));
  }
}
function openApproval(proposal) {
  activeProposal=proposal.id;byId('dialogOrder').textContent='Order '+displayId(proposal.orderId);byId('dialogReason').textContent=proposal.reason;
  const stale=proposal.policyVersion!==followupPolicy.version||followupPolicy.mode!=='approval_required';byId('dialogPolicy').textContent=stale?'Policy changed; a new review is required.':'Policy follow-up-v'+proposal.policyVersion+' requires human approval. Approval creates an internal task only.';byId('approve').disabled=stale;byId('reject').disabled=stale;
  const order=orders.find(o=>o.id===proposal.orderId);byId('dialogPaymentStatus').textContent=order?(order.status==='PAID'?'Captured in PayPal sandbox':'Created - payment not captured'):'Unavailable';
  byId('decisionReason').value='';byId('dialogError').hidden=true;byId('approvalDialog').showModal();
}
byId('orderSearch').addEventListener('input',renderOrders);
byId('productSearch').addEventListener('input',renderProducts);
byId('approvalPendingTab').addEventListener('click',()=>{approvalFilter='pending';renderApprovals()});
byId('approvalResolvedTab').addEventListener('click',()=>{approvalFilter='resolved';renderApprovals()});


function renderTasks() {
  const target=byId('tasksList');clear(target);
  if(!tasks.length)return empty(target,'No internal follow-up tasks yet.');
  for(const task of tasks){
    const row=node('div','list-row'),info=node('div','row-main');
    info.append(node('strong','','Order '+displayId(task.orderId)+' - '+task.status),node('small','',task.description));
    row.append(info,badge('Internal task','blue'));target.append(row);
  }
}




let csrfToken = '';
async function refreshCsrf() {
  const result = await api('/api/auth/csrf');
  csrfToken = result.token || '';
}
async function startSession() {
  try {
    await refreshCsrf();
    const session = await api('/api/auth/session');
    byId('loginSetup').hidden = session.configured;
    if (!session.authenticated) {
      if (!byId('loginDialog').open) byId('loginDialog').showModal();
      return;
    }
    if (byId('loginDialog').open) byId('loginDialog').close();
    byId('logoutButton').hidden = false;
    byId('currentUserName').textContent = session.username || 'Administrator';
    await refresh();
  } catch (error) { notify(error.message); }
}
byId('loginForm').addEventListener('submit', async event => {
  event.preventDefault();
  const error = byId('loginError'); error.hidden = true;
  try {
    await post('/api/auth/login', { username: byId('loginUsername').value.trim(), password: byId('loginPassword').value });
    byId('loginPassword').value = '';
    await startSession();
  } catch (failure) {
    byId('loginPassword').value = '';
    error.textContent = failure.message === 'Request failed (401).' ? 'Invalid username or password.' : failure.message;
    error.hidden = false;
  }
});
byId('loginDialog').addEventListener('cancel', event => event.preventDefault());
byId('logoutButton').addEventListener('click', async () => {
  try {
    await post('/api/auth/logout', {});
    byId('logoutButton').hidden = true;
    orders=[];proposals=[];audit=[];roles=[];agents=[];tasks=[];
    renderOrders();renderApprovals();renderAudit();renderIdentities();renderTasks();
    await startSession();
  } catch (error) { notify(error.message); }
});
startSession();






function renderPolicy() {
  byId('policyVersion').textContent = 'Version ' + followupPolicy.version;
  byId('policyMode').value = followupPolicy.mode;
  const target=byId('policyDetails');clear(target);
  const rule=node('div','identity-entry');
  rule.append(node('strong','','Create internal follow-up'),node('small','','Mode: '+followupPolicy.mode.replaceAll('_',' ')),node('small','','Version: '+followupPolicy.version));
  target.append(rule);
  byId('approvalPolicyMode').textContent=followupPolicy.mode.replaceAll('_',' ');
}
byId('policyForm').addEventListener('submit',async event=>{
  event.preventDefault();const reason=byId('policyReason').value.trim();
  if(!reason)return notify('A reason is required to change the policy.');
  try{await post('/api/policies/followup',{mode:byId('policyMode').value,reason});byId('policyReason').value='';await refresh();notify('Policy updated. Older pending approvals require a new review.')}
  catch(error){notify(error.message)}
});

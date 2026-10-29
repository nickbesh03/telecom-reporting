import {test} from 'node:test';
import assert from 'node:assert/strict';
import {query} from '../src/api.js';
test('filters omit empty values and encode region',()=>{const q=query({from:'2026-10-15',to:'',region:'Север',stationId:2});const p=new URLSearchParams(q);assert.equal(p.get('region'),'Север');assert.equal(p.get('stationId'),'2');assert.equal(p.has('to'),false);});

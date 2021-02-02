import { Injectable } from '@angular/core';
import {webSocket, WebSocketSubject} from 'rxjs/webSocket';
import { EMPTY, Subject } from 'rxjs';

const WS_ENDPOINT =
  'wss://acjjyzgsld.execute-api.ap-southeast-1.amazonaws.com/Prod';

@Injectable({
  providedIn: 'root',
})
export class WebsocketService {
  public socket$: WebSocketSubject<any>;

  public connect(): void {
	this.socket$ = webSocket(WS_ENDPOINT);
  }

  sendMessage(msg: any) {
    this.socket$.next(msg);
  }

  close() {
    this.socket$.complete();
  }
}

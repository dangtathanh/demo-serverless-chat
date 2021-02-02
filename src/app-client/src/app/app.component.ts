import { Component, NgZone } from '@angular/core';
import { WebsocketService } from './services/websocket.service';
import { Message } from '../models/message';

@Component({
  selector: 'app-root',
  templateUrl: './app.component.html',
  styleUrls: ['./app.component.css'],
  providers: [WebsocketService],
})
export class AppComponent {
  title = 'app-client';
  txtMessage: string = '';
  uniqueID: string = new Date().getTime().toString();
  shortName: string = this.makeid(2);
  messages = new Array<Message>();
  message = new Message();
  constructor(private ws: WebsocketService,
    private _ngZone: NgZone  ) {
    this.ws.connect();

    this.ws.socket$.subscribe(
      (msg) => this._ngZone.run(() => {
        if (msg.clientUniqueId !== this.uniqueID) {  
          msg.type = "received";  
          this.messages.push(msg);  
        }  
      }),
      // Called whenever there is a message from the server
      (err) => console.log(err),
      // Called if WebSocket API signals some kind of error
      () => console.log('complete')
      // Called when connection is closed (for whatever reason)
    );
  }

  private makeid(length) {
    var result           = '';
    var characters       = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ';
    var charactersLength = characters.length;
    for ( var i = 0; i < length; i++ ) {
       result += characters.charAt(Math.floor(Math.random() * charactersLength));
    }
    return result.toUpperCase();
 }
 

  sendMessage() {
    console.log('new message from client to websocket: ', this.txtMessage);
    if (this.txtMessage) {  
      this.message = new Message();  
      this.message.clientUniqueId = this.uniqueID;  
      this.message.senderShortName = this.shortName;
      this.message.type = "sent";  
      this.message.body = this.txtMessage;  
      this.message.date = new Date();  
      this.messages.push(this.message);  
      var data = { message: 'sendmessage', data: JSON.stringify(this.message) };
      this.ws.sendMessage(data);
      this.txtMessage = '';
    }  
  }
}

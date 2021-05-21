
var ws_override = "" ;
var debug_add_player = "";

var follow = "";
var mapLayer;
var fogLayer;
var map;
var players = {};
var pins = {};
var server_config = {};
const icons = {};
var statusObj= {};
const iconlist = ["dot", "dotx", "ore", "orex", "crypt","cryptx", "portal", "portalx", "start", "player"];

iconlist.forEach(function(iconname){
    icons[iconname] = L.icon({
        iconUrl: ws_override + 'icon_'+iconname+'.png',
        iconSize:     [30, 30], // size of the icon
        iconAnchor:   [15, 15], // point of the icon which will correspond to marker's location
    });
});


const parseVector3 = str => {
    const strParts = str.split(',');
    return {
        x: parseFloat(strParts[0]),
        y: parseFloat(strParts[1]),
        z: parseFloat(strParts[2]),
    };
};

$(document).ready ( function(){

    $('#sendChat').click(function(){

       const textToSend = $('#chatbox').val();
       if(textToSend){
           //addLog("Sent chat: "+ textToSend);
           $('#chatbox').val("");
           const url = ws_override + 'api/msg/?msg='+textToSend;
           fetch(url, {mode: 'cors'})
               .then(res => res.text())
               .then(text => {
                   map.closePopup();
               });
       }
    });
    map = L.map('map', {
        crs: L.CRS.Simple,
        minZoom: -5
    });
    statusObj.firstPlayer = true;
    statusObj.firstPin = true;

    var bounds = [[-12300,-12300], [12300,12300]];

    mapLayer = L.imageOverlay(ws_override + 'map', bounds, {opacity: 0, zIndex:0}).addTo(map);
    fogLayer = L.imageOverlay(ws_override + 'fog', bounds, {opacity: 1, zIndex:1}).addTo(map);
    fogLayer.on('load', function(){
        mapLayer.setOpacity(1);
    });

    L.control.mousePosition().addTo(map);

    map.setView( [0, 0], 0);
    var hash = new L.Hash(map);

    map.on('click', addMarker);


    const actions = {
        say: (lines, message) => {
            const messageParts = message.split('\n');
            addLog("<strong>" + messageParts[2] + ": " + messageParts[3] + "</strong>")
            console.log(messageParts);
        },
        webchat: (lines, message) => {
            const messageParts = message.split('\n');
            addLog("<strong>" + messageParts[0] + ": " + messageParts[1] + "</strong>")
            console.log(messageParts);
        },
        players: (lines, message) => {
            const msg = message.replace(/^players\n/, '');
            const playerSections = msg.split('\n\n');
            const playerData = [];
            playerSections.forEach(playerSection => {
                const playerLines = playerSection.split('\n');
                const newPlayer = {
                    id: playerLines[0],
                    name: playerLines[1],
                    health: parseFloat(playerLines[3]),
                    maxHealth: parseFloat(playerLines[4])
                };

                if (playerLines[2] !== 'hidden') {
                    const xyz = playerLines[2].split(',').map(parseFloat);
                    newPlayer.x = parseFloat(xyz[0]);
                    newPlayer.y = parseFloat(xyz[1]);
                    newPlayer.z = parseFloat(xyz[2]);
                } else {
                    newPlayer.hidden = true;
                }
                playerData.push(newPlayer);

                if(players[newPlayer.id] == undefined){
                    if(!statusObj.firstPlayer){
                        addLog("Player " + newPlayer.name+" logged in");
                    }else{
                        addLog("Player " + newPlayer.name+" is online");

                    }
                    players[newPlayer.id] = newPlayer;
                    if(!newPlayer.hidden){
                        players[newPlayer.id].marker = L.marker([ newPlayer.z, newPlayer.x ],{icon: icons['player']}).addTo(map);
                        players[newPlayer.id].marker.bindTooltip(newPlayer.name,
                            {
                                permanent: true,
                                direction: 'bottom',
                                className: "my-label",
                                offset: [0, 0],
                            });

                    }
                    players[newPlayer.id].lastUpdate = Date.now();

                }else{
                    if(!newPlayer.hidden){
                        if(players[newPlayer.id].marker == undefined){
                            players[newPlayer.id].marker = L.marker([ newPlayer.z, newPlayer.x ],{icon: icons['player']}).addTo(map);
                            players[newPlayer.id].marker.bindTooltip(newPlayer.name,
                                {
                                    permanent: true,
                                    direction: 'bottom',
                                    className: "my-label",
                                    offset: [0, 0],
                                });
                        }
                        players[newPlayer.id].health = parseFloat(playerLines[3]);
                        players[newPlayer.id].maxHealth= parseFloat(playerLines[4]);


                        players[newPlayer.id].marker.setLatLng(new L.LatLng(newPlayer.z, newPlayer.x ));
                        if(follow == newPlayer.name){
                            map.panTo([ newPlayer.z, newPlayer.x ]);

                        }

                    }else{
                        // Player is hidden
                    }

                    players[newPlayer.id].lastUpdate = Date.now();
                }

            });
            statusObj.firstPlayer=false;

        },
        ping: (lines) => {
            const xz = lines[2].split(',');
            const ping = {
                playerId: lines[0],
                name: lines[1],
                x: parseFloat(xz[0]),
                z: parseFloat(xz[1])
            };
            console.log("ping", ping);

        },
        pin: (lines) => {
            const xz = lines[4].split(',').map(parseFloat);
            const pin = {

                id: lines[1],   // pin id
                type: lines[2], // icon
                text: lines[5],
                x: parseFloat(xz[0]),
                z: parseFloat(xz[1]),
                uid: lines[0],  // steam id
                name: lines[3] // who created it (username)

            };

            setupPin(pin);
            statusObj.firstPin=false;

        },
        rmpin: (lines) => {
            const pin_id = lines[0];
            removePin(pin_id);
        }
    };

    let connectionTries = 0;

    var removePin = function(pin_id){
        if(pins[pin_id] != undefined) {
            pins[pin_id].marker.unbindTooltip();
            map.removeLayer(pins[pin_id].marker)
            delete pins[pin_id];
        }
    };



    var setupPin = function(pin){
        if(isNaN(pin.x) || isNaN(pin.z)){
            return;
        }
        if(pin.text == undefined || !pin.text){
            return;
        }

        // TODO FIXME: this does not work for updates for some reason. will always cause new to run
        // console.log(Object.keys(pins)); <- does not contain id
        // console.log(pins); <- contains id

        if(pins[pin.id] === undefined){
            // We have received a pin from server that we have not seen
            pins[pin.id] = pin;
            if(icons[pin.type] == undefined){
                pin.type = "player";
            }
            pins[pin.id].marker = L.marker([ pin.z, pin.x ], {icon: icons[pin.type], draggable: true, autoPan: true}).addTo(map);
            pins[pin.id].marker.bindTooltip(pin.text, {
                    permanent: true,
                    direction: 'bottom',
                    className: "my-label",
                    offset: [0, 0],
                    // opacity: 0
                });

            var popup = "";
            popup += "<strong>Text</strong>: <input type='text' name='pin_text_" + pin.id + "' value='" + pin.text + "'><br>";
            popup += "<strong>Icon</strong>:";
            popup += " <select name='pin_icon_" + pin.id + "'>";
            Object.keys(icons).forEach((idx) => {
                popup += " <option value='"+idx+"' "+(idx==pin.type?"selected='selected'":"")+">"+idx+"</option>";
            });

            popup += " </select><br>";
            popup += "<strong>Created by</strong>: " + pin.name + " (" + pin.uid + ")<br>";
            popup += "<strong>Id</strong>: " + pin.id + "<br>";
            popup += "<strong>Pos</strong>: " + pin.x + " , " + pin.z + "<br>";
            popup += "<button onclick=\"javascript:updatePin('" + pin.id + "');\">Update</button>";
            popup += "<button onclick=\"javascript:deletePin('" + pin.id + "');\">Delete</button>";

            var newPopup = L.popup().
            setContent(popup);

            pins[pin.id].popup = newPopup;
            pins[pin.id].marker.bindPopup(pins[pin.id].popup);
            pins[pin.id].marker.on('moveend', function(e) {
                var newPos = e.target.getLatLng();
                movePin(pin.id,newPos.lng ,newPos.lat);
            });

            if(!statusObj.firstPin){
                addLog("Got new pin from server: " + pin.text);
            }
        }else{

            if(!statusObj.firstPin){
                addLog("Pin was updated from server: " + pin.text);
            }
            // Update to existing pin
            var popup = "";
            popup += "<strong>Text</strong>: <input type='text' name='pin_text_" + pin.id + "' value='" + pin.text + "'><br>";
            popup += "<strong>Icon</strong>:";
            popup += " <select name='pin_icon_" + pin.id + "'>";
            Object.keys(icons).forEach((idx) => {
                popup += " <option value='"+idx+"' "+(idx==pin.type?"selected='selected'":"")+">"+idx+"</option>";
            });
            popup += " </select><br>";

            popup += "<strong>Created by</strong>: " + pin.name + " (" + pin.uid + ")<br>";
            popup += "<strong>Id</strong>: " + pin.id + "<br>";
            popup += "<strong>Pos</strong>: " + pin.x + " , " + pin.z + "<br>";
            popup += "<button onclick=\"javascript:updatePin('" + pin.id + "');\">Update</button>";
            popup += "<button onclick=\"javascript:deletePin('" + pin.id + "');\">Delete</button>";

            pins[pin.id].popup.setContent(popup);
            pins[pin.id].marker.icon = icons[pin.type];
            pins[pin.id].marker.setLatLng(new L.LatLng(pin.z, pin.x ));
            pins[pin.id].marker.unbindTooltip();
            pins[pin.id].marker.bindTooltip(pin.text,
                {
                    permanent: true,
                    direction: 'bottom',
                    className: "my-label",
                    offset: [0, 0],
                    // opacity: 0
                });
            if(!firstRun){
                addLog("Pin was updated from server: " + pin.text);
            }
        }
    };

    const init = () => {
        let hostobj = new URL(location.href);

        let websocketUrl = "ws://"+hostobj.host;
        if(ws_override){
            websocketUrl = ws_override.split('?')[0].replace(/^http/, 'ws');
        }

        const ws = new WebSocket(websocketUrl);
        ws.addEventListener('message', (e) => {
            const message = e.data.trim();
            const lines = message.split('\n');
            const action = lines.shift();
            const actionFunc = actions[action];
            if (actionFunc) {
                actionFunc(lines, message);
            } else {
                console.log("unknown websocket message: ", e.data);
            }
        });

        ws.addEventListener('open', () => {
            connectionTries = 0;
        });

        ws.addEventListener('close', () => {
            connectionTries++;
            const seconds = Math.min(connectionTries * (connectionTries + 1), 120);
            setTimeout(init, seconds * 1000);
        });
    };

    fetch(ws_override + 'config')
        .then(res => res.text())
        .then(text => {
            server_config = JSON.parse(text);
            const start_pos = parseVector3(server_config.world_start_pos);
            L.marker([ start_pos.z, start_pos.x ], {icon: icons['start'], draggable: false, autoPan: true}).addTo(map);
            addLog("Server config downloaded");

        });

    fetch(ws_override + 'pins')
        .then(res => res.text())
        .then(text => {
            const lines = text.split('\n');
            lines.forEach(line => {
                const lineParts = line.split(',');
                if (lineParts.length > 5) {
                    let pin = {
                        id: lineParts[1],
                        type: lineParts[2],
                        text: lineParts[6],
                        x: parseFloat(lineParts[4]),
                        z: parseFloat(lineParts[5]),
                        uid: lineParts[0],
                        name: lineParts[3],
                        static: true
                    };
                    setupPin(pin);
                }
            });
            addLog("Pins loaded from server");
            firstRun=false;
        });

    setInterval(() => {
        // Update fog if players online
        if(!!Object.keys(players).length){
            fogLayer.setUrl(ws_override+'fog?' + new Date().getTime());
        }
    }, 5000);

    setInterval(() => {
        // Remove logged out players

        const now = Date.now();
        Object.keys(players).forEach((key) => {
            const player = players[key];
            if (now - player.lastUpdate > 5000) {
                addLog("player "+player.name+" logged out ");
                if(follow == player.name){
                    follow = "";
                }
                map.removeLayer(player.marker)
                delete players[key];
            }
        });

        // Update top right playerlist
        let playerHtml = "";
        if(!!Object.keys(players).length) {
            Object.keys(players).forEach((key) => {
                const player = players[key];
                let health_percent = (player.health*100)/(player.maxHealth);
                let color = "red";
                if(health_percent > 75){
                    color = "darkgreen";
                }else if(health_percent > 30){
                    color = "yellow";
                }
                var followIcon = "";
                if(follow == player.name){
                    followIcon = "<a href=\"javascript:unfollowPlayer();\"><i class=\"ri-flag-fill ri-lg\"></i></a>";
                }else{
                    followIcon = "<a href=\"javascript:followPlayer('"+player.name+"');\"><i class=\"ri-flag-line ri-lg\"></i></a>";
                }
                playerHtml += "<div class='playerLine'>" + followIcon + " " + player.name + " ("+player.health+"/"+player.maxHealth+") <div class='hpbarOuter'><div class=\"hpbar\" style='background-color: "+color+"; width: "+health_percent+"%;'>&nbsp;</div></div></div>";

            });
        }else{
            playerHtml = "None";
        }
        $('#overlayPlayers').html(playerHtml);

    }, 1000);

    init();
});

function addMarker(e){
    var popup = "";
    popup += "<strong>Text</strong>: <input type='text' name='pin_text_new' value=''><br>";
    popup += "<strong>Icon</strong>:";
    popup += " <select name='pin_icon_new'>";
    Object.keys(icons).forEach((idx) => {
        popup += " <option value='"+idx+"' "+(idx=="dot"?"selected='selected'":"")+">"+idx+"</option>";
    });

    popup += " </select><br>"; popup += "<strong>Pos</strong>: " + e.latlng.lng + " , " + e.latlng.lat + "<br>";
    popup += "<button onclick=\"javascript:createNewPin('"+e.latlng.lng+"','"+e.latlng.lat+"');\">Create pin</button>";

    L.popup()
        .setLatLng(e.latlng)
        .setContent(popup)
        .openOn(map);
}

const createNewPin = function (posx, posz){
    if(!posx || !posz){
        return;
    }

    const steamid = "0";
    const username = "web";

    let textfield = $("[name='pin_text_new']");
    let text = textfield.val();
    if(!text){
        return;
    }

    let iconfield = $("[name='pin_icon_new']");
    let icon = iconfield.val();

    const url = ws_override + 'api/pin/new/?posx='+posx+'&posz='+posz+'&icon='+icon+'&steamid='+steamid+'&username='+username+'&text=' + text;
    fetch(url, {mode: 'cors'})
        .then(res => res.text())
        .then(text => {
            map.closePopup();
        });
}

const updatePin = function(pin_id){
    if(!pin_id){
        return;
    }
    const pin = pins[pin_id];

    let field = $("[name='pin_text_"+pin_id+"']");
    let newVal = field.val(); //.replace(/ /g,"_");
    if(!newVal){
        alert("Cannot complete, empty text");
        return;
    }
    let iconfield = $("[name='pin_icon_"+pin_id+"']");
    let icon = iconfield.val();

    const url = ws_override + 'api/pin/' + pin.id + '?icon='+icon+'&text=' + newVal;
    fetch(url, {mode: 'cors'})
        .then(res => res.text())
        .then(text => {
            map.closePopup();
        });
}

const movePin = function(pin_id, posx, posz){
    if(!pin_id || posx == undefined || posz == undefined){
        return;
    }
    alertify.confirm("Please confirm", "Are you sure you want to move '"+pins[pin_id].text+"' ?",
        function(){
            const url = ws_override + 'api/pin/' + pin_id + '?posx=' + posx + '&posz=' + posz;
            fetch(url, {mode: 'cors'})
                .then(res => res.text())
                .then(text => {
                    map.closePopup();
                });
        },
        function(){
            pins[pin_id].marker.setLatLng(new L.LatLng(pins[pin_id].z, pins[pin_id].x));
        });
/*
    if(confirm()){



    }else{
        // move back pin

    }

 */
};

const deletePin = function(pin_id){
    if(!pin_id){
        return;
    }

    if(confirm("Are you sure you want to delete '"+pins[pin_id].text+"'?")){
        const url = ws_override + 'api/pin/' + pin_id + '?removePin=1';
        fetch(url, {mode: 'cors'})
            .then(res => res.text())
            .then(text => {
                map.closePopup();
            });
    }
}

const addLog = function(text){
    $('#overlayLog').html(new Date().toLocaleTimeString() + ": " + text + "<br>" + $('#overlayLog').html());
   // $('#overlayLog').html("player " + player.name + " logged out <br>" + $('#overlayLog').html());
}

const unfollowPlayer = function(){
    follow = "";
}

const followPlayer = function(playername){
    follow = playername;
}

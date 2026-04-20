import socket, time

s = socket.socket()
s.connect(("localhost", 9999))

# Test all 3 phases
states = [
    b'{"boss_hp":0.80,"phase":0,"last_move":"attack","player_hp":0.90}\n',
    b'{"boss_hp":0.45,"phase":1,"last_move":"dodge","player_hp":0.60}\n',
    b'{"boss_hp":0.15,"phase":2,"last_move":"special","player_hp":0.30}\n',
]

for state in states:
    print(f"Sending: {state.decode().strip()}")
    s.sendall(state)
    time.sleep(2.5)
    data = s.recv(512).decode().strip()
    print(f"Taunt  : {data}\n")

s.close()
print("Done.")
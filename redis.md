docker exec hexwar-redis redis-cli ping
// 레디스 상태 체크 커멘드

docker exec -it hexwar-redis redis-cli
// 레디스 cli 접속 

127.0.0.1:6379> KEYS *
// KEY 조회 -> gameroom:* 형식으로 저장되어있음

127.0.0.1:6379> PUBSUB CHANNELS game_events:*
// pubsub 채널 조회

127.0.0.1:6379> SUBSCRIBE game_events:6a36de0d
// 구독을 통한 실시간 게임 이벤트 수신 상태 확인 
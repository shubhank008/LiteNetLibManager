using System.Collections.Generic;
using UnityEngine;

namespace LiteNetLibManager
{
    public class DefaultInterestManager : BaseInterestManager
    {
        [Tooltip("Update every ? seconds")]
        public float updateInterval = 1f;

        private float _updateCountDown;
        private readonly HashSet<uint> _subscribingsPool = new HashSet<uint>();

        public override void Setup(LiteNetLibGameManager manager)
        {
            base.Setup(manager);
            _updateCountDown = updateInterval;
        }

        public override void UpdateInterestManagement(float deltaTime)
        {
            _updateCountDown -= deltaTime;
            if (_updateCountDown > 0)
                return;
            _updateCountDown = updateInterval;
            foreach (LiteNetLibPlayer player in Manager.GetPlayers())
            {
                if (!player.IsReady)
                {
                    // Don't subscribe if player not ready
                    continue;
                }
                foreach (LiteNetLibIdentity playerObject in player.GetSpawnedObjects())
                {
                    // Update subscribing list, it will unsubscribe objects which is not in this list
                    _subscribingsPool.Clear();
                    foreach (LiteNetLibIdentity spawnedObject in Manager.Assets.GetSpawnedObjects())
                    {
                        if (ShouldSubscribe(playerObject, spawnedObject))
                            _subscribingsPool.Add(spawnedObject.ObjectId);
                    }
                    playerObject.UpdateSubscribings(_subscribingsPool);
                }
            }
        }
    }
}
